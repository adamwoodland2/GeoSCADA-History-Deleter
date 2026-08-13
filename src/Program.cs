using System;
using System.Runtime.CompilerServices;
using System.Windows.Forms;

namespace HistoryDeleter
{
    internal static class Program
    {
        [STAThread]
        private static void Main()
        {
            // The Geo SCADA client assemblies live in the product install directory, not next to this
            // exe, and ClearScada.Client.dll also P/Invokes the native DBClient.dll. Both have to be
            // wired up before any ClearScada type is touched, so this must happen first and the real
            // entry point must be a separate non-inlined method (its JIT is what pulls the types in).
            string error;
            if (!GeoScadaClient.Initialise(out error))
            {
                Application.EnableVisualStyles();
                MessageBox.Show(error, "Geo SCADA client not found",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            RunApplication();
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void RunApplication()
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            using (DisclaimerForm disclaimer = new DisclaimerForm())
            {
                if (disclaimer.ShowDialog() != DialogResult.Yes) return;
            }

            Session session;
            using (LoginForm login = new LoginForm())
            {
                if (login.ShowDialog() != DialogResult.OK) return;
                session = login.Session;
            }

            using (session)
            using (MainForm main = new MainForm(session))
            {
                Application.Run(main);
            }
        }
    }
}
