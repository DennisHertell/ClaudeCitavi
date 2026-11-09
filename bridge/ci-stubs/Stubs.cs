// Compile-only stubs. Signatures mirror the real Citavi 6 add-on entry point
// (verified on Citavi 6: SwissAcademic.Citavi.Shell.CitaviAddOn`1 in Citavi.exe).
namespace SwissAcademic.Citavi.Shell
{
    public class MainForm : System.Windows.Forms.Form
    {
        public object Project { get; set; }
    }

    public abstract class CitaviAddOn<T> where T : System.Windows.Forms.Form
    {
        public virtual void OnHostingFormLoaded(T mainForm) { }
    }
}
