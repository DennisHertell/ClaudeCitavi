using System;
using System.Linq;
using System.Windows.Forms;
using SwissAcademic.Citavi.Shell;

namespace ClaudeCitavi.Bridge
{
    /// <summary>
    /// Start the bridge from a Citavi macro instead of the AddOns folder.
    /// Needed where Citavi's AddOns folder (under Program Files) is not writable
    /// without admin rights. The macro (bridge/macro/StartClaudeBridge.cs) loads this
    /// DLL with Assembly.LoadFrom and calls Start() on Citavi's UI thread.
    /// </summary>
    public static class MacroEntry
    {
        public static string Start()
        {
            var forms = Application.OpenForms.OfType<MainForm>().ToList();
            if (forms.Count == 0) return "Kein Citavi-Projektfenster gefunden.";

            // Attach all open project windows; the active one is attached last.
            var active = Form.ActiveForm as MainForm ?? forms.Last();
            string status = null;
            foreach (var form in forms.Where(f => !ReferenceEquals(f, active)).Concat(new[] { active }))
            {
                status = Addon.Attach(form, showErrors: false);
            }
            BridgeLog.Write("Started via macro: " + status);
            return status;
        }
    }
}
