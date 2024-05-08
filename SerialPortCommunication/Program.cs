using System;
using System.Collections.Generic;
using System.IO.Ports;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json;
using RabbitMQ.Client;
using System.Net.Http;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using SerialPortCommunication.Models;
using static System.Runtime.InteropServices.JavaScript.JSType;

namespace SerialPortCommunication
{
    internal class Program
    {
        static async Task Main(string[] args)
        {
            Console.WriteLine("Initializing Serial Port Communication...");
            var serialManager = new SerialManager();
            await serialManager.InitializeAsync();
            Console.ReadLine(); // Keep the console open
        }
    }

    public class SerialManager
    {
        private readonly SerialPort _serialPort;
        private readonly RabbitMQService _rabbitMQService;
        private Dictionary<string, int> sensorThresholds;
        private readonly HttpClient _httpClient;

        public SerialManager()
        {
            _serialPort = new SerialPort("COM3", 115200, Parity.None, 8, StopBits.One)
            {
                Handshake = Handshake.None,
                ReadTimeout = 1000,
                WriteTimeout = 1000
            };
            _serialPort.DataReceived += OnDataReceived;
            _serialPort.ErrorReceived += OnErrorReceived;

            _rabbitMQService = new RabbitMQService();
            _httpClient = new HttpClient();
            sensorThresholds = new Dictionary<string, int> {
                { "P1", 80 }, { "P2", 75 }, { "P3", 80 },
                { "P4", 65 }, { "P5", 70 }, { "P6", 100 },
                { "P7", 100 }, { "P8", 65 }, { "P9", 75 },
                { "P1B", 100 }
            };
        }

        public async Task InitializeAsync()
        {
            Console.WriteLine("Initializing Async");
            if (!_serialPort.IsOpen)
            {
                _serialPort.Open();
            }

            await StartCycleAsync();
        }

        private async Task StartCycleAsync()
        {
            Console.WriteLine("Start Cycle Async");
            foreach (var sensorCode in sensorThresholds.Keys)
            {
                await HandleSensorCycle(sensorCode);
            }
        }

        private async Task HandleSensorCycle(string sensorCode)
        {
            Console.WriteLine($"Handle Sensor Cycle {sensorCode}");
            try
            {
                string data = await SendCommandAndWaitForData($"{sensorCode} SDATAFULL", "}");
                if (!string.IsNullOrWhiteSpace(data) && !data.EndsWith("}"))
                {
                    ProcessData(data, sensorCode);
                }
                await SendCommandAndWaitForData($"{sensorCode} CLDATA", $"{sensorCode} CLDATA OK");
                await SendCommandAndWaitForData($"{sensorCode} CLDATA2", $"{sensorCode} CLDATA2 OK");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"ERROR in Handle Sensor Cycle {sensorCode}");
                LogError(sensorCode, ex.Message);
            }
        }

        private async Task<string> SendCommandAndWaitForData(string command, string endMarker)
        {
            Console.WriteLine($"Send Command and Wait for Data: {command}, end marker: {endMarker}");
            _serialPort.WriteLine(command);
            return await ReadResponseUntilMarker(endMarker);
        }

        private async Task<string> ReadResponseUntilMarker(string marker)
        {
            Console.WriteLine("Read Response Until Marker");
            StringBuilder response = new StringBuilder();
            var taskCompletionSource = new TaskCompletionSource<string>();
            void handler(object sender, SerialDataReceivedEventArgs args)
            {
                var data = _serialPort.ReadExisting();
                response.Append(data);
                if (data.Contains(marker))
                {
                    _serialPort.DataReceived -= handler;
                    taskCompletionSource.SetResult(response.ToString());
                }
            }
            _serialPort.DataReceived += handler;
            return await taskCompletionSource.Task;
        }

        private void ProcessData(string data, string sensorCode)
        {
            Console.WriteLine($"Process Data: {data}, sensor code: {sensorCode}");
            var events = ParseEvents(data, sensorCode);
            foreach (var evt in events)
            {
                _rabbitMQService.SendMessage(evt);
            }
        }

        private List<Event> ParseEvents(string data, string sensorCode)
        {
            var lines = data.Split(new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries);
            return lines.Skip(1).Select(line => ParseEvent(line, sensorCode)).ToList();
        }

        private Event ParseEvent(string data, string sensorCode)
        {
            var regex = new Regex(@"(?<timestamp>\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}) (?<beaconId>[^,]+?),\s*(?<status>\d{1,3}),\s*(?<actionCode>[FL]1)");
            var match = regex.Match(data);

            if (match.Success)
            {
                string timestamp = match.Groups["timestamp"].Value;
                string beaconId = match.Groups["beaconId"].Value;
                int status = int.Parse(match.Groups["status"].Value);
                string actionCode = match.Groups["actionCode"].Value;

                if (status > sensorThresholds[sensorCode])
                {
                    LogError(sensorCode, $"RSSI {status} exceeds threshold for {sensorCode} in data: '{data}'.");
                    return null;
                }

                return new Event
                {
                    Id = Guid.NewGuid().ToString(),
                    SensorId = sensorCode,
                    EmployeeId = "-",
                    Timestamp = DateTime.Parse(timestamp),
                    ProjectId = "4f24ac1f-6fd3-4a11-9613-c6a564f2bd86",
                    Action = actionCode == "F1" ? 3 : (actionCode == "L1" ? 7 : 7),  // Assuming no other values appear
                    BeaconId = beaconId,
                    Status = status.ToString()
                };
            }
            else
            {
                Console.WriteLine($"Invalid Even Format: {data}, sensor: {sensorCode}");
                LogError(sensorCode, $"Invalid event format: {data}");
                return null;
            }
        }

        private void LogError(string sensorCode, string message)
        {
            string logMessage = $"{DateTime.Now}: Error at {sensorCode} - {message}\n";
            File.AppendAllText($"Errors_{DateTime.Now:yyyyMMdd}.txt", logMessage);
            Console.WriteLine($"ERROR {DateTime.Now}: Error at {sensorCode} - {message}\n");
        }

        private void OnDataReceived(object sender, SerialDataReceivedEventArgs e)
        {
            string data = _serialPort.ReadExisting();
            var lines = data.Split(new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries);
            foreach (var line in lines)
            {
                try
                {
                    var evt = ParseEvent(line, _serialPort.PortName);
                    if (evt != null)
                    {
                        _rabbitMQService.SendMessage(evt);
                    }
                }
                catch (Exception ex)
                {
                    LogError(_serialPort.PortName, $"Failed to parse event from line '{line}': {ex.Message}");
                }
            }
        }

        private void OnErrorReceived(object sender, SerialErrorReceivedEventArgs e)
        {
            Console.WriteLine("ERROR RECEIVED");
            LogError("SerialPort", "Serial port error received.");
        }
    }

    public class RabbitMQService
    {
        private readonly IModel _channel;

        public RabbitMQService()
        {
            var factory = new ConnectionFactory() { HostName = "localhost" };
            var connection = factory.CreateConnection();
            _channel = connection.CreateModel();
            _channel.QueueDeclare(queue: "events", durable: true, exclusive: false, autoDelete: false, arguments: null);
        }

        public void SendMessage(Event evt)
        {
            if (evt == null) return;  // Do not attempt to send null events
            var message = JsonConvert.SerializeObject(evt);
            var body = Encoding.UTF8.GetBytes(message);
            _channel.BasicPublish(exchange: "", routingKey: "events", basicProperties: null, body: body);
        }
    }
}
