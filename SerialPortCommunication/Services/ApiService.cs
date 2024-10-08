﻿using System;
using System.Text;
using System.Threading.Tasks;
using System.Net.Http;
using System.Net.Http.Headers;

namespace SerialPortCommunication.Services
{
    public class ApiService
    {
        private readonly HttpClient _httpClient;

        public ApiService()
        {
            _httpClient = new HttpClient();
        }

        public async Task<string> GetDataAsync(string url)
        {

            Console.WriteLine("GET " + url);
            HttpResponseMessage response = await _httpClient.GetAsync(url);
            if (response.IsSuccessStatusCode)
            {
                return await response.Content.ReadAsStringAsync();
            }
            else
            {
                Console.WriteLine("GER ERROR" + url);
                return null;
            }
        }

        public async Task<string> PostDataAsync(string url, string data)
        {
            //log the request
            Console.WriteLine("POST " + url + " " + data);
            HttpResponseMessage response = await _httpClient.PostAsync(url, new StringContent(data, Encoding.UTF8, "application/json"));
            if (response.IsSuccessStatusCode)
            {
                Console.WriteLine(response.Content.ReadAsStringAsync().ToString());
                return await response.Content.ReadAsStringAsync();
            }
            else
            {
                Console.WriteLine(response.Content.ReadAsStringAsync().ToString());
                Console.WriteLine(response.StatusCode);
                // Tratar erros aqui
                return null;
            }
        }

        public async Task<string> PutDataAsync(string url, string data)
        {
            HttpResponseMessage response = await _httpClient.PutAsync(url, new StringContent(data, Encoding.UTF8, "application/json"));
            if (response.IsSuccessStatusCode)
            {
                return await response.Content.ReadAsStringAsync();
            }
            else
            {
                // Tratar erros aqui
                return null;
            }
        }

        public async Task<string> GetWithoutTokenAsync(string url)
        {
            HttpResponseMessage response = await _httpClient.GetAsync(url);
            if (response.IsSuccessStatusCode)
            {
                return await response.Content.ReadAsStringAsync();
            }
            else
            {
                // Handle errors
                return null;
            }
        }

        public async Task<string> DeleteDataAsync(string url)
        {
            HttpResponseMessage response = await _httpClient.DeleteAsync(url);
            if (response.IsSuccessStatusCode)
            {
                return await response.Content.ReadAsStringAsync();
            }
            else
            {
                // Tratar erros aqui
                return null;
            }
        }

    }
}