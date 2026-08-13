using System;
using System.Drawing;
using System.Globalization;
using System.Windows.Forms;

namespace HistoryDeleter
{
    /// <summary>
    /// A date and time field with millisecond resolution.
    ///
    /// The stock DateTimePicker is no use here: with ShowUpDown it offers only spinners, and either
    /// way it cannot edit fractional seconds at all, which is the one thing this tool depends on.
    /// So this is a text field that accepts the whole value typed directly, plus a drop-down holding
    /// a real calendar, a time box and the usual shortcuts.
    /// </summary>
    internal sealed class DateTimeBox : UserControl
    {
        private const string Format = "yyyy-MM-dd HH:mm:ss.fff";

        private readonly TextBox _text = new TextBox();
        private readonly Button _drop = new Button();
        private DateTime _value = DateTime.Now;
        private bool _updating;

        public event EventHandler ValueChanged;

        public DateTimeBox()
        {
            // Inherit means the parent form's auto-scaling cascades into this control.
            AutoScaleMode = AutoScaleMode.Inherit;
            BorderStyle = BorderStyle.FixedSingle;
            BackColor = SystemColors.Window;
            Size = new Size(196, 23);

            _text.BorderStyle = BorderStyle.None;
            _text.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            _text.Leave += delegate { CommitText(); };
            _text.KeyDown += OnTextKeyDown;

            _drop.Anchor = AnchorStyles.Top | AnchorStyles.Right | AnchorStyles.Bottom;
            _drop.FlatStyle = FlatStyle.Flat;
            _drop.FlatAppearance.BorderSize = 0;
            _drop.BackColor = SystemColors.Control;
            _drop.Text = "▼";
            _drop.TabStop = false;
            _drop.Click += delegate { ShowPopup(); };

            Controls.Add(_text);
            Controls.Add(_drop);
            LayoutChildren();
            Render();
        }

        public DateTime Value
        {
            get { return _value; }
            set
            {
                DateTime trimmed = Trim(value);
                if (trimmed == _value) return;
                _value = trimmed;
                Render();
                if (ValueChanged != null) ValueChanged(this, EventArgs.Empty);
            }
        }

        /// <summary>Milliseconds are as fine as the historian goes, so anything below is dropped.</summary>
        private static DateTime Trim(DateTime value)
        {
            return new DateTime(value.Ticks - value.Ticks % TimeSpan.TicksPerMillisecond, value.Kind);
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            LayoutChildren();
        }

        protected override void OnFontChanged(EventArgs e)
        {
            base.OnFontChanged(e);
            LayoutChildren();
        }

        private void LayoutChildren()
        {
            int button = Math.Max(18, Height - 2);
            _drop.SetBounds(ClientSize.Width - button - 1, 1, button, ClientSize.Height - 2);
            int textHeight = _text.PreferredHeight;
            _text.SetBounds(3, Math.Max(0, (ClientSize.Height - textHeight) / 2),
                ClientSize.Width - button - 6, textHeight);
        }

        private void Render()
        {
            _updating = true;
            _text.Text = _value.ToString(Format, CultureInfo.InvariantCulture);
            _updating = false;
        }

        private void OnTextKeyDown(object sender, KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Down && e.Alt) { ShowPopup(); e.Handled = true; return; }
            if (e.KeyCode == Keys.Enter) { CommitText(); }
        }

        private void CommitText()
        {
            if (_updating) return;
            DateTime parsed;
            if (TryParse(_text.Text, out parsed)) Value = parsed;
            else Render(); // put the last good value back rather than leaving nonsense on screen
        }

        /// <summary>Accepts the full format, and the shorter ones people actually type.</summary>
        internal static bool TryParse(string text, out DateTime value)
        {
            string[] formats =
            {
                "yyyy-MM-dd HH:mm:ss.fff", "yyyy-MM-dd HH:mm:ss", "yyyy-MM-dd HH:mm", "yyyy-MM-dd",
                "yyyy/MM/dd HH:mm:ss.fff", "yyyy/MM/dd HH:mm:ss", "yyyy/MM/dd HH:mm", "yyyy/MM/dd"
            };
            if (DateTime.TryParseExact(text == null ? "" : text.Trim(), formats,
                    CultureInfo.InvariantCulture, DateTimeStyles.None, out value))
            {
                value = Trim(value);
                return true;
            }
            // Fall back to whatever the machine's locale accepts, so a pasted local-format value works.
            if (DateTime.TryParse(text, CultureInfo.CurrentCulture, DateTimeStyles.None, out value))
            {
                value = Trim(value);
                return true;
            }
            return false;
        }

        private void ShowPopup()
        {
            using (DateTimePopup popup = new DateTimePopup(_value))
            {
                Point below = PointToScreen(new Point(0, Height));
                popup.ShowAt(below, FindForm());
                if (popup.Accepted) Value = popup.Value;
            }
            _text.Focus();
        }
    }

    /// <summary>The drop-down: a month calendar, a millisecond-capable time box and shortcuts.</summary>
    internal sealed class DateTimePopup : ScaledForm
    {
        private readonly MonthCalendar _calendar = new MonthCalendar();
        private readonly TextBox _time = new TextBox();
        private readonly Label _timeError = new Label();
        private readonly Label _timeLabel = Caption("Time:", 0, 0, 44);
        private readonly Label _timeHint = new Label();

        public DateTime Value { get; private set; }
        public bool Accepted { get; private set; }

        public DateTimePopup(DateTime initial)
        {
            Value = initial;

            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.Manual;
            ShowInTaskbar = false;
            MinimizeBox = false;
            MaximizeBox = false;
            BackColor = SystemColors.Window;
            Padding = new Padding(1);

            _calendar.MaxSelectionCount = 1;
            _calendar.SetDate(initial.Date);
            _calendar.DateSelected += delegate { };
            _calendar.DoubleClick += delegate { Accept(); };

            _time.Text = initial.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture);
            _time.TextAlign = HorizontalAlignment.Center;
            _time.KeyDown += delegate(object s, KeyEventArgs e)
            {
                if (e.KeyCode == Keys.Enter) { Accept(); e.Handled = true; }
            };

            _timeError.ForeColor = Color.Firebrick;
            _timeError.Text = "";

            _timeHint.ForeColor = SystemColors.GrayText;
            _timeHint.Text = "hh:mm:ss.fff";

            Controls.Add(_calendar);
            Controls.Add(_timeLabel);
            Controls.Add(_time);
            Controls.Add(_timeHint);
            Controls.Add(_timeError);

            ApplyScaling();
        }

        /// <summary>
        /// Laid out here rather than in the constructor because MonthCalendar picks its own size from
        /// the font, and that is only settled once the handle exists and scaling has been applied.
        /// </summary>
        protected override void OnLoad(EventArgs e)
        {
            base.OnLoad(e);

            // Auto-scaling stretches the calendar, and MonthCalendar fills spare height with extra
            // months. Pinning it to one month's worth keeps a single, expected calendar.
            _calendar.Size = _calendar.SingleMonthSize;

            int margin = Scaled(8);
            int gap = Scaled(6);
            int labelWidth = Scaled(44);
            int timeWidth = Scaled(112);
            int hintWidth = Scaled(104);

            int content = Math.Max(_calendar.Width, labelWidth + gap + timeWidth + gap + hintWidth);
            int width = content + margin * 2;

            _calendar.Location = new Point(margin + (content - _calendar.Width) / 2, margin);

            int row = margin + _calendar.Height + gap * 2;
            int fieldHeight = _time.PreferredHeight;

            _timeLabel.SetBounds(margin, row + Scaled(3), labelWidth, Scaled(20));
            _time.SetBounds(margin + labelWidth + gap, row, timeWidth, fieldHeight);
            _timeHint.SetBounds(margin + labelWidth + gap + timeWidth + gap, row + Scaled(3),
                hintWidth, Scaled(20));

            row += fieldHeight + Scaled(4);
            _timeError.SetBounds(margin, row, content, Scaled(18));
            row += Scaled(22);

            int buttonWidth = (content - gap * 2) / 3;
            AddButton("Now", margin, row, buttonWidth, delegate { Set(DateTime.Now); });
            AddButton("Day start", margin + buttonWidth + gap, row, buttonWidth,
                delegate { Set(SelectedDate()); });
            AddButton("Day end", margin + (buttonWidth + gap) * 2, row, buttonWidth,
                delegate { Set(SelectedDate().AddDays(1).AddMilliseconds(-1)); });

            row += Scaled(26) + gap;

            int okWidth = Scaled(84);
            Button cancel = AddButton("Cancel", margin + content - okWidth * 2 - gap, row, okWidth,
                delegate { Close(); });
            Button ok = AddButton("OK", margin + content - okWidth, row, okWidth, delegate { Accept(); });
            AcceptButton = ok;
            CancelButton = cancel;

            ClientSize = new Size(width, row + Scaled(26) + margin);
            KeepOnScreen();
        }

        private Button AddButton(string text, int left, int top, int width, EventHandler onClick)
        {
            Button button = new Button();
            button.SetBounds(left, top, width, Scaled(26));
            button.Text = text;
            button.Click += onClick;
            Controls.Add(button);
            return button;
        }

        private DateTime SelectedDate() { return _calendar.SelectionStart.Date; }

        private void Set(DateTime value)
        {
            _calendar.SetDate(value.Date);
            _time.Text = value.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture);
            _timeError.Text = "";
        }

        private void Accept()
        {
            TimeSpan time;
            if (!TryParseTime(_time.Text, out time))
            {
                _timeError.Text = "Enter a time as hh:mm:ss.fff";
                _time.Focus();
                _time.SelectAll();
                return;
            }
            Value = SelectedDate().Add(time);
            Accepted = true;
            Close();
        }

        private static bool TryParseTime(string text, out TimeSpan time)
        {
            time = TimeSpan.Zero;
            string[] formats = { @"hh\:mm\:ss\.fff", @"hh\:mm\:ss", @"hh\:mm", @"h\:mm\:ss\.fff", @"h\:mm\:ss", @"h\:mm" };
            return TimeSpan.TryParseExact(text == null ? "" : text.Trim(), formats,
                CultureInfo.InvariantCulture, out time);
        }

        public void ShowAt(Point screenLocation, Form owner)
        {
            Location = screenLocation;
            ShowDialog(owner);
        }

        private void KeepOnScreen()
        {
            Rectangle screen = Screen.FromPoint(Location).WorkingArea;
            int x = Math.Min(Location.X, screen.Right - Width - 4);
            int y = Location.Y;
            if (y + Height > screen.Bottom) y = Math.Max(screen.Top, y - Height - Scaled(24));
            Location = new Point(Math.Max(screen.Left, x), y);
        }

        protected override void OnDeactivate(EventArgs e)
        {
            base.OnDeactivate(e);
            // Clicking away from a drop-down should dismiss it, as it would for any other.
            Close();
        }
    }
}
