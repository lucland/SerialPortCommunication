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
using SerialPortCommunication.Repositories;
using SerialPortCommunication.Services;

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
        private string currentSensor;
        private readonly List<string> _sensors;

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
            currentSensor = "P1";
            _sensors = sensorThresholds.Keys.ToList();
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
            while (true) // Loop to continuously cycle through sensors
            {
                foreach (var sensor in _sensors)
                {
                    if (!await HandleSensorCycle(sensor))
                    {
                        Console.WriteLine($"Failed to handle {sensor}, skipping to next.");
                    }
                }
            }
        }

        private async Task<bool> HandleSensorCycle(string sensorCode)
        {
            try
            {
                if (!await SendCommandAndWaitForConfirmation($"{sensorCode} OK", $"{sensorCode} Yes"))
                {
                    Console.WriteLine($"No response or incorrect response from {sensorCode}, skipping to next sensor.");
                    return false;
                }

                string data = await SendCommandAndWaitForData($"{sensorCode} SDATAFULL", "}");
                if (!string.IsNullOrWhiteSpace(data) && !data.Contains("}"))
                {
                    ProcessData(data, sensorCode);
                }
                await Task.Delay(50); // Small delay before sending clean up commands
                await SendCommandAndWaitForData($"{sensorCode} CLDATA", $"{sensorCode} CLDATA OK");
                await SendCommandAndWaitForData($"{sensorCode} CLDATA2", $"{sensorCode} CLDATA2 OK");
                return true;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error during cycle with {sensorCode}: {ex.Message}");
                return false;
            }
        }

        private async Task<bool> SendCommandAndWaitForConfirmation(string command, string expectedResponse)
        {
            _serialPort.WriteLine(command);
            await Task.Delay(50); // Let the buffer fill
            var timer = new System.Timers.Timer(3000);
            bool receivedCorrectResponse = false;

            void handler(object sender, SerialDataReceivedEventArgs args)
            {
                var response = _serialPort.ReadExisting();
                if (response.Contains(expectedResponse))
                {
                    receivedCorrectResponse = true;
                    timer.Stop(); // Stop the timer on receiving correct response
                }
            }

            _serialPort.DataReceived += handler;
            timer.Elapsed += (sender, args) => {
                _serialPort.DataReceived -= handler; // Unsubscribe from event
                timer.Stop();
            };
            timer.Start();
            await Task.Delay(3000); // Wait for the response or timeout
            _serialPort.DataReceived -= handler; // Ensure handler is unsubscribed after timeout

            return receivedCorrectResponse;
        }

        private async Task<string> SendCommandAndWaitForData(string command, string endMarker)
        {
            Console.WriteLine($"Sending command for data: {command}");
            _serialPort.WriteLine(command);
            StringBuilder response = new StringBuilder();
            var completionSource = new TaskCompletionSource<string>();

            void handler(object sender, SerialDataReceivedEventArgs args)
            {
                var data = _serialPort.ReadExisting();
                response.Append(data);
                if (data.Contains(endMarker))
                {
                    _serialPort.DataReceived -= handler;
                    completionSource.SetResult(response.ToString());
                }
            }

            _serialPort.DataReceived += handler;
            return await completionSource.Task;
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

        private async void ProcessData(string data, string sensorCode)
        {
            Console.WriteLine($"Process Data: {data}, sensor code: {sensorCode}");
            var events = ParseEvents(data, sensorCode);
            foreach (var evt in events)
            {
                await _rabbitMQService.SendMessageAsync(evt);
            }
        }

        private List<Event> ParseEvents(string data, string sensorCode)
        {
            var lines = data.Split(new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries);
            return lines.Skip(1).Select(line => ParseEvent(line, sensorCode)).ToList();
        }


        private Event ParseEvent(string data, string sensorCode)
        {
             Console.WriteLine($"Process Data: {data}, sensor code: {sensorCode}");
            var regex = new Regex(@"(?<timestamp>\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}) (?<beaconId>[^,]+?),((?<status>\d+),)?(?<actionCode>[FL]1)");
            var match = regex.Match(data);

            if (match.Success)
            {
                string timestamp = match.Groups["timestamp"].Value;
                string beaconId = match.Groups["beaconId"].Value;
                string actionCode = match.Groups["actionCode"].Value;

                // Check if the status group was captured, otherwise default to 0
                int status = match.Groups["status"].Success ? int.Parse(match.Groups["status"].Value) : 0;

                // Check if the action code ends with F1 or L1
                if (actionCode == "L1" && match.Groups["status"].Success == false)
                {
                    status = 0; // Ensure status is 0 if actionCode is L1 and no status provided
                }

                return new Event
                {
                    Id = Guid.NewGuid().ToString(),
                    SensorId = sensorCode,
                    EmployeeId = "-",
                    Timestamp = DateTime.Parse(timestamp),
                    ProjectId = "projectid",
                    Action = actionCode == "F1" ? 3 : 7,
                    BeaconId = beaconId,
                    Status = status.ToString()
                };
            }
            else
            {
                Console.WriteLine($"Invalid event format: {data}, sensor: {sensorCode}");
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
            Console.WriteLine($"On Data Received: {data}");
            var lines = data.Split(new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries);
            foreach (var line in lines)
            {
                try
                {
                    Console.WriteLine($"On Data Received LINE: {line}");
                    var evt = ParseEvent(line, currentSensor);
                    if (evt != null)
                    {
                        _rabbitMQService.SendMessageAsync(evt);
                    }
                }
                catch (Exception ex)
                {
                    LogError(currentSensor, $"Failed to parse event from line '{line}': {ex.Message}");
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
        private readonly EventRepository _eventRepository;

        public RabbitMQService()
        {
            _eventRepository = new EventRepository(apiService: new ApiService());
            var factory = new ConnectionFactory() { HostName = "localhost" };
            var connection = factory.CreateConnection();
            _channel = connection.CreateModel();
            _channel.QueueDeclare(queue: "events", durable: true, exclusive: false, autoDelete: false, arguments: null);
        }

        public async Task SendMessageAsync(Event evt)
        {
            if (evt == null) return;  // Do not attempt to send null events
            var message = JsonConvert.SerializeObject(evt);
            var body = Encoding.UTF8.GetBytes(message);
            _channel.BasicPublish(exchange: "", routingKey: "events", basicProperties: null, body: body);

            // Send the event to the backend
            await SendToBackendAsync(evt);
        }

        private async Task SendToBackendAsync(Event evt)
        {
           await _eventRepository.CreateEventAsync(evt);
        }
    }
}
