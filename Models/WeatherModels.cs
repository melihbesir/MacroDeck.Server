using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace MacroDeck.Server.Models;

public class HourlyForecast
{
    [JsonPropertyName("time")]
    public string Time { get; set; } = string.Empty; // "02:00", "05:00"

    [JsonPropertyName("temp")]
    public int Temp { get; set; }

    [JsonPropertyName("precipitation")]
    public int Precipitation { get; set; } // %

    [JsonPropertyName("windSpeed")]
    public int WindSpeed { get; set; } // km/h

    [JsonPropertyName("weatherCode")]
    public int WeatherCode { get; set; }
}

public class DayForecast
{
    [JsonPropertyName("date")]
    public string Date { get; set; } = string.Empty; // "2026-10-24"

    [JsonPropertyName("dayName")]
    public string DayName { get; set; } = string.Empty; // "Paz", "Pzt"

    [JsonPropertyName("maxTemp")]
    public int MaxTemp { get; set; }

    [JsonPropertyName("minTemp")]
    public int MinTemp { get; set; }

    [JsonPropertyName("condition")]
    public string Condition { get; set; } = string.Empty; // "Çok bulutlu"

    [JsonPropertyName("weatherCode")]
    public int WeatherCode { get; set; }

    [JsonPropertyName("hourly")]
    public List<HourlyForecast> Hourly { get; set; } = new();
}

public class WeatherData
{
    [JsonPropertyName("cityName")]
    public string CityName { get; set; } = "İstanbul";

    [JsonPropertyName("currentTemp")]
    public int CurrentTemp { get; set; } = 19;

    [JsonPropertyName("currentCondition")]
    public string CurrentCondition { get; set; } = "Çok bulutlu";

    [JsonPropertyName("currentWeatherCode")]
    public int CurrentWeatherCode { get; set; } = 3;

    [JsonPropertyName("humidity")]
    public int Humidity { get; set; } = 70; // %

    [JsonPropertyName("windSpeed")]
    public int WindSpeed { get; set; } = 10; // km/h

    [JsonPropertyName("precipitation")]
    public int Precipitation { get; set; } = 10; // %

    [JsonPropertyName("lastUpdated")]
    public string LastUpdated { get; set; } = string.Empty;

    [JsonPropertyName("days")]
    public List<DayForecast> Days { get; set; } = new();

    public static string GetWeatherDescription(int code)
    {
        return code switch
        {
            0 => "Açık",
            1 => "Çoğunlukla açık",
            2 => "Parçalı bulutlu",
            3 => "Çok bulutlu",
            45 or 48 => "Sisli",
            51 or 53 or 55 => "Çiseleyen yağmur",
            61 or 63 or 65 => "Yağmurlu",
            66 or 67 => "Dondurucu yağmur",
            71 or 73 or 75 => "Karlı",
            77 => "Kar taneleri",
            80 or 81 or 82 => "Sağanak yağış",
            85 or 86 => "Kar sağanağı",
            95 => "Gök gürültülü fırtına",
            96 or 99 => "Dolu ve fırtına",
            _ => "Bulutlu"
        };
    }
}
