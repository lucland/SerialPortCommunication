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
        private string receivedData = "";
        private readonly ManualResetEventSlim responseReceived = new ManualResetEventSlim(false);
        private string lastResponse = string.Empty;

        public SerialManager()
        {
            _serialPort = new SerialPort("COM3", 115200, Parity.None, 8, StopBits.One)
            {
                Handshake = Handshake.None,
                ReadTimeout = 5000,
                WriteTimeout = 5000
            };
            _serialPort.DataReceived += OnDataReceived;
            _serialPort.ErrorReceived += OnErrorReceived;

            _rabbitMQService = new RabbitMQService();
            _httpClient = new HttpClient();
            sensorThresholds = new Dictionary<string, int> {
                { "P1B", 75 }, { "P5", 75 }, { "P2", 75 }, 
                { "P7", 75 }, { "P4", 75 }, { "P3", 80 }, { "P8", 75 }, { "P9", 75 }
            };
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
                    await HandleSensorCycle(sensor);
                }
            }
        }

        private async Task HandleSensorCycle(string sensorCode)
        {
            Console.WriteLine($"Handling sensor: {sensorCode}");
            string command = $"{sensorCode} SDATAFULL";
            _serialPort.WriteLine(command);
            StringBuilder data = new StringBuilder();
            var lastDataReceivedTime = DateTime.Now;

            while (true)
            {
                if (_serialPort.BytesToRead > 0)
                {
                    string received = _serialPort.ReadExisting();
                    data.Append(received);
                    Console.WriteLine($"DATA from {sensorCode}: {received}");
                    lastDataReceivedTime = DateTime.Now; // Reset timer on data received
                }

                if (DateTime.Now - lastDataReceivedTime > TimeSpan.FromSeconds(3))
                {
                    Console.WriteLine($"Timeout or end of data block from {sensorCode}");
                    break; // Break the loop if no data for 3 seconds
                }

                await Task.Delay(100); // Reduce CPU usage
            }

            // Process data if it's a valid block (ignoring empty or just opened commands)
            if (!string.IsNullOrWhiteSpace(data.ToString()) && data.ToString().Contains("}"))
            {
                Console.WriteLine($"Processing data from {sensorCode}");
                ProcessData(data.ToString(), sensorCode);
            }
            else
            {
                Console.WriteLine($"No valid data received from {sensorCode} or incomplete data block.");
            }

            // Cleaning commands
            _serialPort.WriteLine($"{sensorCode} CLDATA");
            _serialPort.WriteLine($"{sensorCode} CLDATA2");
        }





        private void ProcessData(string data, string sensorCode)
        {
            Console.WriteLine($"Processing data for {sensorCode}");
            var events = ParseEvents(data, sensorCode);
            foreach (var evt in events)
            {
                _rabbitMQService.SendMessageAsync(evt);
            }
        }

        private List<Event> ParseEvents(string data, string sensorCode)
        {
            var lines = data.Split(new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries);
            return lines.Skip(1).TakeWhile(line => !line.Contains("}")).Select(line => ParseEvent(line, sensorCode)).Where(evt => evt != null).ToList();
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
                    ProjectId = "4f24ac1f-6fd3-4a11-9613-c6a564f2bd86",
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
