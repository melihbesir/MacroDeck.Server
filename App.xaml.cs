using System.Configuration;
using System.Data;
using System.Windows;

namespace MacroDeck.Server;

/// <summary>
/// Interaction logic for App.xaml
/// </summary>
public partial class App : System.Windows.Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        if (e.Args.Contains("--test-sensors"))
        {
            try
            {
                var mon = new MacroDeck.Server.Services.SystemMonitorService();
                var stats = mon.CollectStats();
                System.IO.File.WriteAllText(@"c:\Users\melih\source\MacroDeck\test_result.json", System.Text.Json.JsonSerializer.Serialize(stats, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
            }
            catch (Exception ex)
            {
                System.IO.File.WriteAllText(@"c:\Users\melih\source\MacroDeck\test_error.txt", ex.ToString());
            }
            Shutdown();
            return;
        }

        base.OnStartup(e);
    }
}

