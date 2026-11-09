// Citavi macro: starts the ClaudeCitavi Bridge in the running Citavi.
// Use this when the add-on cannot be installed into Citavi's AddOns folder
// (no admin rights). Run once after every Citavi start:
//   Citavi > Extras > Makro-Editor (Alt+F11) > open this file > Ausführen (F5)
//
// The install script copies ClaudeCitavi.Bridge.dll to %APPDATA%\ClaudeCitavi.

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
            MessageBox.Show("Nicht gefunden: " + dll + "\n\nBitte zuerst scripts\\install.ps1 -Macro ausführen.", "ClaudeCitavi");
            return;
        }

        var result = Assembly.LoadFrom(dll)
            .GetType("ClaudeCitavi.Bridge.MacroEntry", true)
            .GetMethod("Start")
            .Invoke(null, null);

        MessageBox.Show(Convert.ToString(result), "ClaudeCitavi");
    }
}
