using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;

namespace HistoryDeleter
{
    /// <summary>
    /// Searches the database for objects that actually store historic data, so the user does not have
    /// to know a point's full name by heart.
    /// </summary>
    internal sealed class PointPickerForm : ScaledForm
    {
        private const int MaxResults = 500;

        private readonly Session _session;
        private readonly TextBox _search = new TextBox();
        private readonly Button _find = new Button();
        private readonly ListView _results = new ListView();
        private readonly Label _status = new Label();
        private readonly Button _ok = new Button();

        public PointMatch Selected { get; private set; }

        public PointPickerForm(Session session, string initialText)
        {
            _session = session;

            Text = "Find a point with historic data";
            StartPosition = FormStartPosition.CenterParent;
            ClientSize = new Size(760, 480);
            MinimumSize = new Size(560, 360);

            Label prompt = new Label();
            prompt.SetBounds(12, 15, 62, 20);
            prompt.Text = "Contains:";

            _search.SetBounds(78, 12, ClientSize.Width - 78 - 106, 23);
            _search.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            _search.Text = initialText == null ? "" : initialText.Trim();

            _find.SetBounds(ClientSize.Width - 94, 11, 82, 25);
            _find.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            _find.Text = "Search";
            _find.Click += delegate { Search(); };

            _results.SetBounds(12, 46, ClientSize.Width - 24, ClientSize.Height - 46 - 76);
            _results.Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;
            _results.View = View.Details;
            _results.FullRowSelect = true;
            _results.MultiSelect = false;
            _results.HideSelection = false;
            _results.Columns.Add("Full name");
            _results.Columns.Add("Aggregate");
            _results.Columns.Add("Id");
            _results.Resize += delegate { LayoutColumns(); };
            _results.SelectedIndexChanged += delegate { _ok.Enabled = _results.SelectedItems.Count > 0; };
            _results.DoubleClick += delegate { if (_results.SelectedItems.Count > 0) Accept(); };

            _status.SetBounds(12, ClientSize.Height - 66, ClientSize.Width - 24, 20);
            _status.Anchor = AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;
            _status.ForeColor = SystemColors.GrayText;

            _ok.SetBounds(ClientSize.Width - 188, ClientSize.Height - 38, 84, 27);
            _ok.Anchor = AnchorStyles.Bottom | AnchorStyles.Right;
            _ok.Text = "Select";
            _ok.Enabled = false;
            _ok.Click += delegate { Accept(); };

            Button cancel = new Button();
            cancel.SetBounds(ClientSize.Width - 96, ClientSize.Height - 38, 84, 27);
            cancel.Anchor = AnchorStyles.Bottom | AnchorStyles.Right;
            cancel.Text = "Cancel";
            cancel.DialogResult = DialogResult.Cancel;

            Controls.AddRange(new Control[] { prompt, _search, _find, _results, _status, _ok, cancel });
            AcceptButton = _find;
            CancelButton = cancel;
            ApplyScaling();
            LayoutColumns();
        }

        /// <summary>
        /// ListView column widths are device pixels that auto-scaling leaves alone, so the name
        /// column is given whatever is left over after the two fixed columns and the scroll bar.
        /// </summary>
        private void LayoutColumns()
        {
            if (_results.Columns.Count < 3) return;
            int aggregate = Scaled(100);
            int id = Scaled(80);
            // ClientSize already excludes the scroll bar once it appears, but not before, so always
            // reserve room for it rather than flip-flopping a horizontal bar in and out.
            int name = _results.ClientSize.Width - aggregate - id
                       - SystemInformation.VerticalScrollBarWidth - Scaled(2);
            _results.Columns[0].Width = System.Math.Max(Scaled(120), name);
            _results.Columns[1].Width = aggregate;
            _results.Columns[2].Width = id;
        }

        private void Search()
        {
            string text = _search.Text.Trim();
            if (text.Length == 0)
            {
                _status.Text = "Type part of a point name to search for.";
                return;
            }

            // Let a user paste a name that already contains wildcards; otherwise search anywhere.
            string pattern = text.IndexOf('%') >= 0 ? text : "%" + text + "%";

            List<PointMatch> matches;
            try
            {
                UseWaitCursor = true;
                _status.Text = "Searching...";
                Application.DoEvents();
                matches = _session.SearchPoints(pattern, MaxResults);
            }
            catch (Exception ex)
            {
                _status.Text = "Search failed: " + ex.Message;
                return;
            }
            finally
            {
                UseWaitCursor = false;
            }

            _results.BeginUpdate();
            _results.Items.Clear();
            foreach (PointMatch match in matches)
            {
                ListViewItem item = new ListViewItem(match.FullName);
                item.SubItems.Add(match.Aggregate);
                item.SubItems.Add(match.Id.ToString());
                item.Tag = match;
                _results.Items.Add(item);
            }
            _results.EndUpdate();

            _status.Text = matches.Count == 0
                ? "No historic points matched '" + text + "'."
                : matches.Count + " match" + (matches.Count == 1 ? "" : "es") +
                  (matches.Count >= MaxResults ? " (limit reached, refine the search)" : "");
        }

        private void Accept()
        {
            if (_results.SelectedItems.Count == 0) return;
            Selected = (PointMatch)_results.SelectedItems[0].Tag;
            DialogResult = DialogResult.OK;
            Close();
        }
    }
}
