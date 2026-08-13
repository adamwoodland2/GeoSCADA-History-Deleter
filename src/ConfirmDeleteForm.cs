using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;

namespace HistoryDeleter
{
    /// <summary>
    /// Last stop before history is destroyed. Spells out exactly what is about to go, at the
    /// historian's full timestamp resolution.
    /// </summary>
    internal sealed class ConfirmDeleteForm : ScaledForm
    {
        public ConfirmDeleteForm(string pointName, string aggregate, List<HistoricRecord> records,
            bool dryRun)
        {
            Text = dryRun ? "Preview deletion (dry run)" : "Confirm permanent deletion";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            StartPosition = FormStartPosition.CenterParent;
            MaximizeBox = false;
            MinimizeBox = false;
            // Height is finalised once the detail rows have been laid out.
            ClientSize = new Size(560, 416);

            Label headline = new Label();
            headline.SetBounds(16, 14, 528, 40);
            headline.Font = new Font(Font.FontFamily, Font.Size + 1.5f, FontStyle.Bold);
            headline.ForeColor = dryRun ? SystemColors.ControlText : Color.Firebrick;
            headline.Text = dryRun
                ? "Preview " + records.Count + " record" + (records.Count == 1 ? "" : "s") + " (nothing will be deleted)"
                : "Permanently delete " + records.Count + " historic record" + (records.Count == 1 ? "" : "s") + "?";

            HistoricRecord first = records[0];
            HistoricRecord last = records[records.Count - 1];

            int y = 62;
            y = AddDetail("Point", pointName, y);
            y = AddDetail("Aggregate", aggregate, y);
            y = AddDetail("Method", "IHistory.DeleteHistoricData", y);
            // Shown at the historian's full resolution, not the millisecond the grid displays, so
            // there is no doubt about which instants are being addressed.
            y = AddDetail("Earliest", first.ExactLocalText + " local", y);
            y = AddDetail("", first.ExactUtcText + " UTC", y);
            y = AddDetail("Latest", last.ExactLocalText + " local", y);
            y = AddDetail("", last.ExactUtcText + " UTC", y);
            int instants = DistinctInstants(records);
            y = AddDetail("Records", instants == records.Count
                ? records.Count.ToString()
                : records.Count + "  (sharing " + instants +
                  (instants == 1 ? " timestamp)" : " timestamps)"), y);

            Label warning = new Label();
            warning.SetBounds(16, y + 6, 528, 34);
            warning.ForeColor = Color.Firebrick;
            warning.Text = dryRun
                ? "Dry run is enabled, so this will only report what would happen."
                : "This cannot be undone. The selected values are removed from the historian.";

            Label journalNote = new Label();
            journalNote.SetBounds(16, y + 44, 528, 34);
            journalNote.ForeColor = SystemColors.GrayText;
            journalNote.Text = "Geo SCADA logs each deletion against the point with your user name, " +
                               "client address and the record's timestamp.";

            ClientSize = new Size(ClientSize.Width, y + 130);

            Button ok = new Button();
            ok.SetBounds(368, ClientSize.Height - 40, 88, 27);
            ok.Text = dryRun ? "Preview" : "Delete";
            ok.DialogResult = DialogResult.OK;

            Button cancel = new Button();
            cancel.SetBounds(464, ClientSize.Height - 40, 88, 27);
            cancel.Text = "Cancel";
            cancel.DialogResult = DialogResult.Cancel;

            Controls.AddRange(new Control[] { headline, warning, journalNote, ok, cancel });
            AcceptButton = null; // Enter must not fire a destructive default button.
            CancelButton = cancel;
            ApplyScaling();
        }

        private static int DistinctInstants(List<HistoricRecord> records)
        {
            Dictionary<long, bool> seen = new Dictionary<long, bool>();
            foreach (HistoricRecord record in records) seen[record.Time.UtcTicks] = true;
            return seen.Count;
        }

        private int AddDetail(string caption, string value, int top)
        {
            Label captionLabel = new Label();
            captionLabel.SetBounds(16, top, 108, 18);
            captionLabel.Text = caption.Length == 0 ? "" : caption + ":";
            captionLabel.ForeColor = SystemColors.GrayText;

            Label valueLabel = new Label();
            valueLabel.SetBounds(128, top, 416, 18);
            valueLabel.Text = value;
            valueLabel.AutoEllipsis = true;

            Controls.Add(captionLabel);
            Controls.Add(valueLabel);
            return top + 22;
        }
    }
}
