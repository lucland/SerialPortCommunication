﻿using Newtonsoft.Json;
using SerialPortCommunication.Services;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace SerialPortCommunication.Repositories
{
    public abstract class BaseRepository<T> where T : new()
    {
        private ApiService _apiService;

        protected BaseRepository(ApiService apiService)
        {
            _apiService = apiService ?? throw new ArgumentNullException(nameof(apiService));
        }

        protected async Task<string> GetAsync(string url)
        {
            try
            {
                Console.WriteLine($"GET {url}");
                return await _apiService.GetDataAsync(url);
            }
            catch
            {
                Console.WriteLine($"GET {url} failed. Fetching data from LiteDB.");
                return "";
            }
        }

        protected async Task<string> PutAsync(string url, string data, string collectionName)
        {
            try
            {
                var item = JsonConvert.DeserializeObject<T>(data);
                if (item == null) throw new InvalidOperationException("Deserialized item is null.");

                return await _apiService.PutDataAsync(url, data);
            }
            catch (Exception ex)
            {
                // Log the exception if necessary
                Console.WriteLine($"An error occurred: {ex.Message}");

                var item = JsonConvert.DeserializeObject<T>(data);
                if (item == null) throw new InvalidOperationException("Deserialized item is null.");

                return JsonConvert.SerializeObject(item);
            }
        }

        //put method without LiteDB
        protected async Task<string> PutAsyncNoDB(string url, string data)
        {
            return await _apiService.PutDataAsync(url, data);
        }

        protected async Task<string> PostAsync(string url, string data, string collectionName)
        {
            try
            {
                var item = JsonConvert.DeserializeObject<T>(data);
                if (item == null) throw new InvalidOperationException("Deserialized item is null.");

                return await _apiService.PostDataAsync(url, data);
            }
            catch (Exception ex)
            {
                // Log the exception if necessary
                Console.WriteLine($"An error occurred: {ex.Message}");

                var item = JsonConvert.DeserializeObject<T>(data);
                if (item == null) throw new InvalidOperationException("Deserialized item is null.");

                return JsonConvert.SerializeObject(item);
            }
        }
    }
}