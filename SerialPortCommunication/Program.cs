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
        private readonly List<string> _sensorSequence;
        private readonly HttpClient _httpClient;
        private readonly string _apiBaseUrl = "https://your-api-url.com";

        public SerialManager()
        {
            _serialPort = new SerialPort("COM5", 115200, Parity.None, 8, StopBits.One)
            {
                Handshake = Handshake.None,
                ReadTimeout = 1000,
                WriteTimeout = 1000
            };
            _serialPort.DataReceived += OnDataReceived;
            _serialPort.ErrorReceived += OnErrorReceived;
            _rabbitMQService = new RabbitMQService();
            _httpClient = new HttpClient();
            _sensorSequence = new List<string> { "P1", "P2", "P3", "P4", "P5", "P6", "P7", "P8", "P9", "P10", "P11", "P12", "P1B" }; // Initial order can be changed as needed
        }

        public async Task InitializeAsync()
        {
            if (!_serialPort.IsOpen)
            {
                _serialPort.Open();
            }

            await StartCycleAsync();
        }

        private async Task StartCycleAsync()
        {
            foreach (var sensorCode in _sensorSequence)
            {
                await HandleSensorCycle(sensorCode);
            }
        }

        private async Task HandleSensorCycle(string sensorCode)
        {
            try
            {
                await SendCommandAndWaitForData($"{sensorCode} OK", $"{sensorCode} Yes");
                string data = await SendCommandAndWaitForData($"{sensorCode} SDATAFULL", "}");
                string value = "}";
                if (!string.IsNullOrWhiteSpace(data) && !data.Contains(value) && !data.Contains("{" + sensorCode))
                {
                    ProcessData(data, sensorCode);
                }
                await SendCommandAndWaitForData($"{sensorCode} CLDATA", $"{sensorCode} CLDATA OK");
                await SendCommandAndWaitForData($"{sensorCode} CLDATA2", $"{sensorCode} CLDATA2 OK");
            }
            catch (Exception ex)
            {
                LogError(sensorCode, ex.Message);
            }
        }

        private async Task<string> SendCommandAndWaitForData(string command, string endMarker)
        {
            _serialPort.WriteLine(command);
            return await ReadResponseUntilMarker(endMarker);
        }

        private async Task<string> ReadResponseUntilMarker(string marker)
        {
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
            // Regex ajustada para extrair os componentes corretos e ignorar qualquer coisa após F1 ou L1
            var regex = new Regex(@"(?<timestamp>\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}) (?<beaconId>[^,]+?),\s*(?<status>\d{1,3}),\s*(?<actionCode>[FL]1)");
            var match = regex.Match(data.Trim());

            if (match.Success)
            {
                string timestamp = match.Groups["timestamp"].Value;
                string beaconId = match.Groups["beaconId"].Value.Trim();
                string status = match.Groups["status"].Value.Trim();
                string actionCode = match.Groups["actionCode"].Value.Trim();

                return new Event
                {
                    Id = Guid.NewGuid().ToString(),
                    SensorId = sensorCode,
                    EmployeeId = "-",  // as specified
                    Timestamp = DateTime.Parse(timestamp),
                    ProjectId = "projectid",
                    Action = actionCode == "F1" ? 3 : (actionCode == "L1" ? 7 : 0),  // Assuming no other values appear
                    BeaconId = beaconId,
                    Status = status
                };
            }
            else
            {
                // Here you should log or handle the failure to parse effectively
                Console.WriteLine($"Failed to parse event from data: '{data}'.");
                return null; // Or throw an exception depending on your error handling strategy
            }
        }

        private void LogError(string sensorCode, string message)
        {
            string logMessage = $"{DateTime.Now}: Error at {sensorCode} - {message}\n";
            File.AppendAllText($"Errors_{DateTime.Now:yyyyMMdd}.txt", logMessage);
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
            var message = JsonConvert.SerializeObject(evt);
            var body = Encoding.UTF8.GetBytes(message);
            _channel.BasicPublish(exchange: "", routingKey: "events", basicProperties: null, body: body);
        }
    }
}


//STL = PROXIMO E LIBERADO
//STB = PROXIMO E BLOQUEADO
//F0 = FIND NAO CADASTRADO
//F1 = FIND CADASTRADO
//L0 = LOST NAO CADASTRADO
//L1 = LOST CADASTRADO

//P0 manda para todos

//SDATA = SOLICITA DADOS
//CLDATA = LIMPA DADOS
//CLALL = LIMPA TODOS OS DADOS
//CL,xx:xx:xx:xx:xx:xx = LIMPA UM DADO ESPECIFICO
//A = APROVADO
//RSSI,-10 = RSSI MINIMO, APEMAS NUMEROS NEGATIVOS
//SL = RETORNA LISTA COMPLETA DE CADASTRADOS
//PX ST,2023-12-18 13:52:44
//CLP xx:xx:xx:xx:xx:xx = apaga beacon do portalo
//PX CLALLPLID = apaga todos os beacons do portalo
//PX SPLID = mostra os beacons do portalo
//PX PLID,xx:xx:xx:xx:xx:xx = adiciona beacon ao portalo
//PX MAXRSSI,-100 = seta o rssi minimo para o portalo

//P1 BTV
//P2 BATERRY VOLTAGE: 14.19

//Example of the data sent by the slave after a PN SDATA command:
/*
{PN
2024-01-11 14:24:42 ff:ff:10:e2:34:06,L1
2024-01-11 14:25:08 ff:ff:10:e2:34:06,F1
2024-01-11 14:25:37 ff:ff:10:e2:34:06,F1 STL
2024-01-11 14:25:39 ff:ff:10:e2:34:06,F1 STL
2024-01-11 14:45:37 ff:ff:10:e2:34:06,F1
2024-01-11 14:45:57 ff:ff:10:e2:33:64,F0 STB
2024-01-11 14:45:59 ff:ff:10:e2:33:64,F0 STB
2024-01-11 14:46:07 ff:ff:10:e2:33:64,F0 STB
2024-01-11 14:46:43 ff:ff:10:e2:33:64,F0 STB
2024-01-11 14:48:11 ff:ff:10:e2:34:06,F1
2024-01-11 14:50:58 ff:ff:10:e2:34:06,L1
}
*/

/*
BUSINESS RULES

Here is the Buisiness Logic I want to implement and the current code I have, can you analyze the code, think about the possible error scenarios and improve the code as whole so it turns into a consistent and error proof code?

What it must do:
 - This code is a Master, which communicates with N slaves via Serial Port communication, it must send data to the slaves, retrieve data, process the data and send the data to the backend successfully.
 - It should work 100% asyncronously, and it should never stop with an exception or timeout, it will follow a certain cycle and if for some reason the cycle gets broken, it should not interfeer with the rest of the software and restart the cycle again, never stopping the cycle.
 - It should display in the UI with the _updateStatusAction function the current step of the cycle and the current PN of the cycle.
- If it returns any exception, for example if we do not receive any awnser of the Slave, we should show a MessageBox to the User and try again the action, never stopping for any reason.


The cycle rules:
 1 - We open the Serial port (if not opened) and retrieve the number of slaves it should cycle through.
2 - If it has only one slave, it will always cycle through it, if there are more then one, it start the cycle of the first one and just start the cycle of the second slave if the first one finishes successfully or get stuck some how, and so on.
3 - The next step of the cycle should just start when the last one finishes or gets stucked somehow.

The cycle process:
1 - The first thing of the cycle is to send the serial command to the slave as "PN OK" where N is number of the slave (P1, P2, P3... and so on).
2 - After sending the OK command, it will keep checking if it receives the "PN Yes" awnser. 
2.1 - If it does not receive any "PN Yes" after 3 seconds, it should send "PN OK" again and wait again for the "PN Yes"
2.2 - If it send the "PN OK" two times and it still didnt receive an awnser,  it should jump to the next slave cycle.
3 - When receiving "PN Yes" successfully, it should send the command "PN SDATA" to request the data load of the slave.
3.1 - The slave will always start the awnser with a "{PN" line
3.2 - The slave will always finish the awnser with a "}" line
3.3 - While we do not receive the "}" awnser, we should retrieve each line, turn each line into a Event object and send this object to the API.
4 - After successfully receiving the "}" and successfully sending all of the received data to the Backend, we should send the command "PN CLDATA", so it clears the fetched data, we will not receive any awwnser for this command
5 - After sending the "PN CLDATA" we start the cycle again with the next PN (or with the same one if it is the only one).

Another functionality it should have:
 - At the beggining of every CYCLE the Master should fetch the approved IDs from the Backend with the GetAllApprovedUsersAsync() method and send it to all Slaves with the "P0 A," command, with each ID separated by comma after the "A," without any space after the "A,".

Plus:
 - The class should have a public pause and resume function, so other parts of the code can use the PORT COM5 without any problem
 - The class should have a function to send the "PN A," command which receives a code to be sent with the command, in case we add a new user during the day, we send the id of the new user with this command as "PN A,xx:xx:xx:xx:xx:xx" where xx:xx:xx:xx:xx:xx is the string structure of the ID
- The code should all be commented
- The code needs to follow the good practices of the .NET and C# conventions.
- The code should be as efficient as possible
- The code needs to handle all the possible error scenarios effectivly

*/