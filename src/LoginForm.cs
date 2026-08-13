using System;
using System.Drawing;
using System.Security;
using System.Windows.Forms;

namespace HistoryDeleter
{
    /// <summary>
    /// Collects connection details. Nothing is written to disk: the server, user name and password
    /// are all re-entered each launch by design.
    /// </summary>
    internal sealed class LoginForm : ScaledForm
    {
        private readonly TextBox _host = new TextBox();
        private readonly TextBox _port = new TextBox();
        private readonly TextBox _user = new TextBox();
        private readonly TextBox _password = new TextBox();
        private readonly Label _status = new Label();
        private readonly Button _connect = new Button();
        private readonly Button _cancel = new Button();

        public Session Session { get; private set; }

        public LoginForm()
        {
            Text = "Connect to Geo SCADA";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            StartPosition = FormStartPosition.CenterScreen;
            MaximizeBox = false;
            MinimizeBox = false;
            ClientSize = new Size(400, 210);

            AddLabel("Server:", 16);
            _host.SetBounds(100, 13, 190, 23);
            _host.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;

            AddLabel("Port:", 46);
            _port.SetBounds(100, 43, 70, 23);
            _port.Text = "5481";

            AddLabel("User name:", 76);
            _user.SetBounds(100, 73, 190, 23);

            AddLabel("Password:", 106);
            _password.SetBounds(100, 103, 190, 23);
            _password.UseSystemPasswordChar = true;

            _status.SetBounds(16, 136, 368, 32);
            _status.ForeColor = Color.Firebrick;

            _connect.SetBounds(214, 172, 80, 26);
            _connect.Text = "Connect";
            _connect.Click += OnConnect;

            _cancel.SetBounds(304, 172, 80, 26);
            _cancel.Text = "Cancel";
            _cancel.DialogResult = DialogResult.Cancel;

            Controls.AddRange(new Control[] { _host, _port, _user, _password, _status, _connect, _cancel });
            AcceptButton = _connect;
            CancelButton = _cancel;
            ApplyScaling();
        }

        private void AddLabel(string text, int top)
        {
            Controls.Add(Caption(text, 16, top, 84));
        }

        private void OnConnect(object sender, EventArgs e)
        {
            int port;
            if (_host.Text.Trim().Length == 0)
            {
                Fail("Enter the Geo SCADA server name or address.");
                return;
            }
            if (!int.TryParse(_port.Text.Trim(), out port) || port <= 0 || port > 65535)
            {
                Fail("Enter a valid port number (the Geo SCADA default is 5481).");
                return;
            }
            if (_user.Text.Trim().Length == 0)
            {
                Fail("Enter a user name.");
                return;
            }

            SetBusy(true);
            try
            {
                using (SecureString password = ToSecureString(_password.Text))
                {
                    Session = Session.Connect(_host.Text.Trim(), port, _user.Text.Trim(), password);
                }
                // The password only ever existed in this text box and the SecureString above.
                _password.Clear();
                DialogResult = DialogResult.OK;
                Close();
            }
            catch (Exception ex)
            {
                Fail(ex.Message);
            }
            finally
            {
                SetBusy(false);
            }
        }

        private static SecureString ToSecureString(string text)
        {
            SecureString secure = new SecureString();
            foreach (char c in text) secure.AppendChar(c);
            secure.MakeReadOnly();
            return secure;
        }

        private void SetBusy(bool busy)
        {
            UseWaitCursor = busy;
            _connect.Enabled = !busy;
            if (busy) _status.Text = "Connecting...";
            _status.ForeColor = busy ? SystemColors.ControlText : Color.Firebrick;
            Application.DoEvents();
        }

        private void Fail(string message)
        {
            _status.ForeColor = Color.Firebrick;
            _status.Text = message;
        }
    }
}
