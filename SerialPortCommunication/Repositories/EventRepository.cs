﻿
using Newtonsoft.Json;
using SerialPortCommunication.Models;
using SerialPortCommunication.Services;
using static System.Net.WebRequestMethods;

namespace SerialPortCommunication.Repositories
{
    public class EventRepository : BaseRepository<Event>
    {
        //private const string BaseUrl = "http://localhost:3000/api/v1/events";
        private string BaseUrl = "https://gmarineinnovation.com/api/v1" + "/events";

        public EventRepository(ApiService apiService)
            : base(apiService)
        {
        }

        public async Task<Event> GetEventByIdAsync(string id)
        {
            var json = await GetAsync($"{BaseUrl}/{id}");
            return JsonConvert.DeserializeObject<Event>(json);
        }

        public async Task<string> GetAllEventsAsync(int limit, int offset)
        {
            return await GetAsync($"{BaseUrl}?limit={limit}&offset={offset}");
        }

        public async Task<List<string>> GetAllAreas()
        {
            var json =  await GetAsync($"https://gmarineinnovation.com/api/v1/employees/areas");
            return JsonConvert.DeserializeObject<List<string>>(json);
        }

        public async Task<Event> CreateEventAsync(Event eventItem)
        {
            Console.WriteLine("Creating event...");
            var json = JsonConvert.SerializeObject(eventItem);
            var response = await PostAsync("https://gmarineinnovation.com/api/v1/events", json, "Event");
            Console.WriteLine("Event created.");
            return JsonConvert.DeserializeObject<Event>(response);
        }

        public async Task<bool> SyncEventsAsync(List<Event> events)
        {
            try
            {
                if (events == null || events.Count == 0)
                {
                    // Handle the case where there are no events to sync.
                    return false;
                }

                if (events.Count > 100)
                {
                    // Handle the case where the batch size is too large.
                    // You might want to break it down into smaller batches here.
                    return false;
                }

                var json = JsonConvert.SerializeObject(new { events });
                var response = await PostAsync($"{BaseUrl}/sync", json, "Event");

                if (response != null)
                {
                    return true; // Sync successful
                }
                else
                {
                    // Handle the case where the sync failed.
                    return false;
                }
            }
            catch (HttpRequestException ex)
            {
                // Handle network/HTTP errors
                // Log the exception or inform the user
                return false;
            }
        }
    }
}