// Citavi-Makro: startet die ClaudeCitavi-Bridge in dieser Citavi-Sitzung (Weg C, Makro-Funktion von Citavi).
// Bei jeder Citavi-Sitzung einmal ausführen: Extras > Makro-Editor (Alt+F11) > Datei öffnen > Ausführen (F5).
//
// Einrichten.cmd (einrichten.py) legt ClaudeCitavi.Bridge.dll und dieses Makro in %APPDATA%\ClaudeCitavi ab.

using System;
using System.IO;
using System.Reflection;
using System.Windows.Forms;

public static class CitaviMacro
{
    public static void Main()
    {
        var dll = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "ClaudeCitavi", "ClaudeCitavi.Bridge.dll");

        if (!File.Exists(dll))
        {
            MessageBox.Show("Nicht gefunden: " + dll + "\n\nBitte zuerst Einrichten aus dem Paket ClaudeCitavi-Makro ausführen.", "ClaudeCitavi");
            return;
        }

        var result = Assembly.LoadFrom(dll)
            .GetType("ClaudeCitavi.Bridge.MacroEntry", true)
            .GetMethod("Start")
            .Invoke(null, null);

        MessageBox.Show(Convert.ToString(result), "ClaudeCitavi");
    }
}
