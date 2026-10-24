using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using System.Timers;
using MacroDeck.Server.Models;

namespace MacroDeck.Server.Services;

public class WeatherService : IDisposable
{
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(10) };
    private readonly System.Timers.Timer _refreshTimer;
    public string CurrentCity { get; private set; } = "İstanbul";
    public WeatherData? LatestData { get; private set; }

    public event Action<WeatherData>? WeatherUpdated;

    public WeatherService()
    {
        // Her 30 dakikada bir otomatik hava durumu güncelle
        _refreshTimer = new System.Timers.Timer(30 * 60 * 1000)
        {
            AutoReset = true
        };
        _refreshTimer.Elapsed += async (s, e) => await RefreshWeatherAsync();
        _refreshTimer.Start();
    }

    public async Task RefreshWeatherAsync(string? cityName = null)
    {
        if (!string.IsNullOrWhiteSpace(cityName))
        {
            CurrentCity = cityName.Trim();
        }

        try
        {
            var data = await FetchFromOpenMeteoAsync(CurrentCity);
            if (data != null)
            {
                LatestData = data;
                WeatherUpdated?.Invoke(data);
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Weather fetch error: {ex.Message}");
        }
    }

    private async Task<WeatherData?> FetchFromOpenMeteoAsync(string city)
    {
        // 1. Şehir koordinatlarını bul (Geocoding)
        double lat = 41.01;
        double lon = 28.97;
        string resolvedCityName = city;

        try
        {
            string geoUrl = $"https://geocoding-api.open-meteo.com/v1/search?name={Uri.EscapeDataString(city)}&count=1&language=tr&format=json";
            string geoJson = await _http.GetStringAsync(geoUrl);
            using var geoDoc = JsonDocument.Parse(geoJson);
            if (geoDoc.RootElement.TryGetProperty("results", out var results) && results.GetArrayLength() > 0)
            {
                var first = results[0];
                lat = first.GetProperty("latitude").GetDouble();
                lon = first.GetProperty("longitude").GetDouble();
                if (first.TryGetProperty("name", out var nameProp))
                {
                    resolvedCityName = nameProp.GetString() ?? city;
                }
            }
        }
        catch { }

        // 2. 7 günlük saatlik ve günlük hava durumu verilerini çek
        string weatherUrl = $"https://api.open-meteo.com/v1/forecast?latitude={lat.ToString(CultureInfo.InvariantCulture)}&longitude={lon.ToString(CultureInfo.InvariantCulture)}&current=temperature_2m,relative_humidity_2m,weather_code,wind_speed_10m&hourly=temperature_2m,precipitation_probability,wind_speed_10m,weather_code&daily=weather_code,temperature_2m_max,temperature_2m_min,precipitation_probability_max&timezone=auto";
        string weatherJson = await _http.GetStringAsync(weatherUrl);
        using var doc = JsonDocument.Parse(weatherJson);
        var root = doc.RootElement;

        var result = new WeatherData
        {
            CityName = resolvedCityName,
            LastUpdated = DateTime.Now.ToString("HH:mm")
        };

        // Current
        if (root.TryGetProperty("current", out var current))
        {
            result.CurrentTemp = (int)Math.Round(current.GetProperty("temperature_2m").GetDouble());
            result.Humidity = current.GetProperty("relative_humidity_2m").GetInt32();
            result.WindSpeed = (int)Math.Round(current.GetProperty("wind_speed_10m").GetDouble());
            result.CurrentWeatherCode = current.GetProperty("weather_code").GetInt32();
            result.CurrentCondition = WeatherData.GetWeatherDescription(result.CurrentWeatherCode);
        }

        // Daily
        if (root.TryGetProperty("daily", out var daily))
        {
            var dates = daily.GetProperty("time");
            var maxTemps = daily.GetProperty("temperature_2m_max");
            var minTemps = daily.GetProperty("temperature_2m_min");
            var weatherCodes = daily.GetProperty("weather_code");
            var precs = daily.GetProperty("precipitation_probability_max");

            int dayCount = dates.GetArrayLength();
            var trCulture = new CultureInfo("tr-TR");

            for (int i = 0; i < Math.Min(dayCount, 7); i++)
            {
                string dateStr = dates[i].GetString() ?? string.Empty;
                DateTime parsedDate = DateTime.TryParse(dateStr, out var d) ? d : DateTime.Now.AddDays(i);

                string dayAbbr = parsedDate.ToString("ddd", trCulture);
                if (dayAbbr.Length > 3) dayAbbr = dayAbbr.Substring(0, 3);
                // İlk harfi büyük yap
                dayAbbr = char.ToUpper(dayAbbr[0]) + dayAbbr.Substring(1);

                int wCode = weatherCodes[i].GetInt32();
                var dayForecast = new DayForecast
                {
                    Date = dateStr,
                    DayName = dayAbbr,
                    MaxTemp = (int)Math.Round(maxTemps[i].GetDouble()),
                    MinTemp = (int)Math.Round(minTemps[i].GetDouble()),
                    WeatherCode = wCode,
                    Condition = WeatherData.GetWeatherDescription(wCode)
                };

                result.Days.Add(dayForecast);
            }

            if (precs.GetArrayLength() > 0)
            {
                result.Precipitation = precs[0].GetInt32();
            }
        }

        // Hourly
        if (root.TryGetProperty("hourly", out var hourly))
        {
            var times = hourly.GetProperty("time");
            var temps = hourly.GetProperty("temperature_2m");
            var precs = hourly.GetProperty("precipitation_probability");
            var winds = hourly.GetProperty("wind_speed_10m");
            var codes = hourly.GetProperty("weather_code");

            int hourlyCount = times.GetArrayLength();

            for (int d = 0; d < result.Days.Count; d++)
            {
                var targetDay = result.Days[d];

                // Her gün için 02:00, 05:00, 08:00, 11:00, 14:00, 17:00, 20:00, 23:00 saatlerini eşleştir
                int startIndex = d * 24;
                int[] targetHours = { 2, 5, 8, 11, 14, 17, 20, 23 };

                foreach (var h in targetHours)
                {
                    int idx = startIndex + h;
                    if (idx < hourlyCount)
                    {
                        targetDay.Hourly.Add(new HourlyForecast
                        {
                            Time = $"{h:D2}:00",
                            Temp = (int)Math.Round(temps[idx].GetDouble()),
                            Precipitation = precs[idx].GetInt32(),
                            WindSpeed = (int)Math.Round(winds[idx].GetDouble()),
                            WeatherCode = codes[idx].GetInt32()
                        });
                    }
                }
            }
        }

        return result;
    }

    public void Dispose()
    {
        _refreshTimer.Dispose();
        _http.Dispose();
    }
}
