using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace HistoryDeleter
{
    internal sealed class MainForm : ScaledForm
    {
        /// <summary>Ceiling on a single query so an over-wide range cannot hang the UI.</summary>
        private const int MaxRows = 50000;

        private readonly Session _session;
        private ResolvedPoint _point;

        /// <summary>Grid row indexes keyed by exact timestamp, for spotting duplicates.</summary>
        private readonly Dictionary<long, List<int>> _rowsByTimestamp = new Dictionary<long, List<int>>();
        private bool _syncingSelection;
        private int _duplicateGroups;

        private readonly TextBox _pointName = new TextBox();
        private readonly Button _browse = new Button();
        private readonly ComboBox _aggregate = new ComboBox();
        private readonly DateTimeBox _start = new DateTimeBox();
        private readonly DateTimeBox _end = new DateTimeBox();
        private readonly Button _query = new Button();
        private readonly DataGridView _grid = new DataGridView();
        private readonly CheckBox _dryRun = new CheckBox();
        private readonly Button _delete = new Button();
        private readonly StatusStrip _statusStrip = new StatusStrip();
        private readonly ToolStripStatusLabel _connectionStatus = new ToolStripStatusLabel();
        private readonly ToolStripStatusLabel _rowStatus = new ToolStripStatusLabel();

        public MainForm(Session session)
        {
            _session = session;

            Text = "Geo SCADA History Deleter";
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(1120, 660);
            MinimumSize = new Size(900, 480);

            Controls.Add(_grid);
            Controls.Add(BuildQueryPanel());
            Controls.Add(BuildActionPanel());
            Controls.Add(BuildStatusStrip());

            BuildGrid();
            SetDefaultRange();
            UpdateConnectionStatus();
            UpdateEnabledState();

            // Start in the point box and let Enter run the query, so the common path is all keyboard.
            ActiveControl = _pointName;
            AcceptButton = _query;
            ApplyScaling();

            // Grid columns are sized in device pixels and are not touched by auto-scaling, so they
            // have to be added once the scale factor is known.
            BuildGridColumns();
        }

        // ---------------------------------------------------------------- layout

        private Panel BuildQueryPanel()
        {
            Panel panel = new Panel();
            panel.Dock = DockStyle.Top;
            panel.Height = 112;
            // Docking will enforce this anyway, but setting it up front means the child coordinates
            // below are computed against the real width rather than Panel's 200px default.
            panel.Width = ClientSize.Width;
            panel.Padding = new Padding(0, 0, 0, 6);

            panel.Controls.Add(Caption("Point:", 12, 15, 52));
            _pointName.SetBounds(66, 12, panel.Width - 66 - 110, 23);
            _pointName.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            _pointName.TextChanged += delegate { _point = null; UpdateEnabledState(); };

            _browse.SetBounds(panel.Width - 98, 11, 86, 25);
            _browse.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            _browse.Text = "Browse...";
            _browse.Click += OnBrowse;

            panel.Controls.Add(Caption("Aggregate:", 12, 47, 70));
            _aggregate.SetBounds(86, 44, 160, 23);
            _aggregate.DropDownStyle = ComboBoxStyle.DropDownList;

            Button lastHour = MakeRangeButton("Last hour", 300, TimeSpan.FromHours(1));
            Button last24 = MakeRangeButton("Last 24 h", 386, TimeSpan.FromHours(24));
            Button last7 = MakeRangeButton("Last 7 days", 472, TimeSpan.FromDays(7));

            panel.Controls.Add(Caption("Start:", 12, 79, 40));
            _start.SetBounds(52, 76, 196, 23);

            panel.Controls.Add(Caption("End:", 262, 79, 34));
            _end.SetBounds(298, 76, 196, 23);

            _query.SetBounds(510, 75, 110, 26);
            _query.Text = "Run Query";
            _query.Click += OnQuery;

            panel.Controls.AddRange(new Control[]
            {
                _pointName, _browse, _aggregate, lastHour, last24, last7,
                _start, _end, _query
            });
            return panel;
        }

        private Panel BuildActionPanel()
        {
            Panel panel = new Panel();
            panel.Dock = DockStyle.Bottom;
            panel.Height = 48;
            panel.Width = ClientSize.Width;

            // There is deliberately no raw/modified selector here: DeleteHistoricData, the only call
            // that can address sub-millisecond timestamps, has no source-type argument.
            _dryRun.SetBounds(12, 14, 280, 20);
            _dryRun.Text = "Dry run (preview only, delete nothing)";

            _delete.SetBounds(panel.Width - 192, 10, 180, 28);
            _delete.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            _delete.Text = "Delete Selected...";
            _delete.Click += OnDelete;

            panel.Controls.AddRange(new Control[] { _dryRun, _delete });
            return panel;
        }

        private StatusStrip BuildStatusStrip()
        {
            _connectionStatus.Spring = true;
            _connectionStatus.TextAlign = ContentAlignment.MiddleLeft;
            _rowStatus.TextAlign = ContentAlignment.MiddleRight;
            _statusStrip.Items.Add(_connectionStatus);
            _statusStrip.Items.Add(_rowStatus);
            return _statusStrip;
        }

        private void BuildGrid()
        {
            _grid.Dock = DockStyle.Fill;
            _grid.ReadOnly = true;
            _grid.AllowUserToAddRows = false;
            _grid.AllowUserToDeleteRows = false;
            _grid.AllowUserToResizeRows = false;
            _grid.RowHeadersVisible = false;
            _grid.BackgroundColor = SystemColors.Window;
            _grid.BorderStyle = BorderStyle.None;
            _grid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
            // Ctrl-click and Shift-click bulk selection come from this.
            _grid.MultiSelect = true;
            _grid.AlternatingRowsDefaultCellStyle.BackColor = Color.FromArgb(246, 246, 246);
            // The default fixed header height does not grow with the font, which clips the captions.
            _grid.ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.AutoSize;
            _grid.SelectionChanged += delegate { ExtendSelectionToDuplicates(); UpdateEnabledState(); };
        }

        /// <summary>
        /// Deletion addresses records by instant, and there is no way to say *which* record at a
        /// shared instant to remove - the server takes the earliest-written one. Half-deleting a
        /// duplicate group is therefore never a meaningful thing to ask for, so selecting one member
        /// selects the whole group and the group is removed together.
        /// </summary>
        private void ExtendSelectionToDuplicates()
        {
            if (_syncingSelection || _rowsByTimestamp.Count == 0) return;
            _syncingSelection = true;
            try
            {
                List<int> toSelect = new List<int>();
                foreach (DataGridViewRow row in _grid.SelectedRows)
                {
                    HistoricRecord record = row.Tag as HistoricRecord;
                    if (record == null) continue;
                    List<int> siblings;
                    if (!_rowsByTimestamp.TryGetValue(record.Time.UtcTicks, out siblings)) continue;
                    if (siblings.Count < 2) continue;
                    foreach (int index in siblings)
                        if (!_grid.Rows[index].Selected) toSelect.Add(index);
                }
                foreach (int index in toSelect) _grid.Rows[index].Selected = true;
            }
            finally
            {
                _syncingSelection = false;
            }
        }

        private void BuildGridColumns()
        {
            // Wide enough for "yyyy-MM-dd HH:mm:ss.fff" plus the cell padding.
            AddColumn("Time (Local)", 186);
            AddColumn("Time (UTC)", 186);
            AddColumn("Value", 84);
            AddColumn("Formatted", 100);
            AddColumn("Quality", 66);
            AddColumn("Quality Desc", 100);
            AddColumn("Reason", 116);
            AddColumn("State", 84);
            AddColumn("Status", 84);
            AddColumn("Modified", 186);
            AddColumn("Modified By", 116);
            AddColumn("Record Id", 260);
        }

        private void AddColumn(string header, int designWidth)
        {
            DataGridViewTextBoxColumn column = new DataGridViewTextBoxColumn();
            column.HeaderText = header;
            column.Width = Scaled(designWidth);
            column.MinimumWidth = Scaled(40);
            column.SortMode = DataGridViewColumnSortMode.Automatic;
            _grid.Columns.Add(column);
        }

        private Button MakeRangeButton(string text, int left, TimeSpan span)
        {
            Button button = new Button();
            button.Text = text;
            button.SetBounds(left, 43, 80, 25);
            TimeSpan captured = span;
            button.Click += delegate
            {
                DateTime now = DateTime.Now;
                SetRange(now - captured, now);
            };
            return button;
        }

        // ---------------------------------------------------------------- time helpers

        private void SetDefaultRange()
        {
            DateTime now = DateTime.Now;
            SetRange(now.AddHours(-24), now);
        }

        private void SetRange(DateTime from, DateTime to)
        {
            _start.Value = from;
            _end.Value = to;
        }

        /// <summary>
        /// Reads a field as a local instant and converts it to a DateTimeOffset, so the server gets
        /// an unambiguous UTC value regardless of the machine's time zone.
        /// </summary>
        private static DateTimeOffset ReadPicker(DateTimeBox box)
        {
            DateTime value = box.Value;
            return new DateTimeOffset(DateTime.SpecifyKind(value, DateTimeKind.Local));
        }

        // ---------------------------------------------------------------- actions

        private void OnBrowse(object sender, EventArgs e)
        {
            using (PointPickerForm picker = new PointPickerForm(_session, _pointName.Text))
            {
                if (picker.ShowDialog(this) != DialogResult.OK || picker.Selected == null) return;
                _pointName.Text = picker.Selected.FullName;
                ResolvePoint();
            }
        }

        private bool ResolvePoint()
        {
            string fullName = _pointName.Text.Trim();
            if (fullName.Length == 0)
            {
                ShowWarning("Enter a point name, or use Browse to search for one.");
                return false;
            }

            try
            {
                UseWaitCursor = true;
                _point = _session.ResolvePoint(fullName);
            }
            catch (Exception ex)
            {
                _point = null;
                ShowError("Could not look up the point.", ex);
                return false;
            }
            finally
            {
                UseWaitCursor = false;
            }

            if (_point == null)
            {
                ShowWarning("No object named '" + fullName + "' exists on this server.");
                return false;
            }
            if (_point.Aggregates.Count == 0)
            {
                ShowWarning("'" + _point.FullName + "' exists but stores no historic data, " +
                            "so there is nothing to query or delete.");
                _point = null;
                return false;
            }

            _aggregate.Items.Clear();
            foreach (string name in _point.Aggregates) _aggregate.Items.Add(name);
            _aggregate.SelectedIndex = 0;
            _aggregate.Enabled = _point.Aggregates.Count > 1;
            UpdateEnabledState();
            return true;
        }

        private void OnQuery(object sender, EventArgs e)
        {
            RunQuery();
        }

        /// <summary>Runs the query, fills the grid and returns the records, or null if it failed.</summary>
        private List<HistoricRecord> RunQuery()
        {
            if (_point == null && !ResolvePoint()) return null;

            DateTimeOffset start = ReadPicker(_start);
            DateTimeOffset end = ReadPicker(_end);
            if (end < start)
            {
                ShowWarning("The end time is before the start time.");
                return null;
            }

            List<HistoricRecord> records;
            try
            {
                UseWaitCursor = true;
                _query.Enabled = false;
                records = _session.ReadHistory(_point.Id, start, end, MaxRows);
            }
            catch (Exception ex)
            {
                ShowError("The historic query failed.", ex);
                return null;
            }
            finally
            {
                UseWaitCursor = false;
                _query.Enabled = true;
            }

            Populate(records);

            string message = records.Count + " record" + (records.Count == 1 ? "" : "s") +
                             " from " + start.ToLocalTime().ToString(TimeFormats.Display) +
                             " to " + end.ToLocalTime().ToString(TimeFormats.Display) + " (local)";
            if (records.Count >= MaxRows)
                message += "  -  newest " + MaxRows + " shown, narrow the time range to see the rest";

            int unresolved = 0;
            foreach (HistoricRecord record in records) if (!record.HasExactTime) unresolved++;
            if (unresolved > 0)
                message += "  -  " + unresolved + " record(s) have no exact timestamp and cannot be deleted";
            if (_duplicateGroups > 0)
                message += "  -  " + _duplicateGroups + " duplicate timestamp group(s), highlighted; " +
                           "these delete together";

            _rowStatus.Text = message;
            return records;
        }

        private void Populate(List<HistoricRecord> records)
        {
            _grid.SuspendLayout();
            _syncingSelection = true;
            _grid.Rows.Clear();
            _rowsByTimestamp.Clear();
            foreach (HistoricRecord record in records)
            {
                int index = _grid.Rows.Add(
                    record.LocalText,
                    record.UtcText,
                    record.Value == null ? "" : Convert.ToString(record.Value, CultureInfo.CurrentCulture),
                    record.FormattedValue,
                    record.Quality.ToString(CultureInfo.CurrentCulture),
                    record.QualityDesc,
                    record.ReasonDesc,
                    record.StateDesc,
                    record.StatusDesc,
                    FormatModTime(record.ModTime),
                    record.ModUser,
                    record.RecordId);
                // The row carries the original record, so deletion uses the server's own timestamp.
                DataGridViewRow row = _grid.Rows[index];
                row.Tag = record;
                if (!record.HasExactTime)
                {
                    row.DefaultCellStyle.ForeColor = Color.Firebrick;
                    row.Cells[0].ToolTipText =
                        "This record's exact timestamp could not be determined, so it cannot be deleted.";
                }

                List<int> sameTime;
                if (!_rowsByTimestamp.TryGetValue(record.Time.UtcTicks, out sameTime))
                {
                    sameTime = new List<int>();
                    _rowsByTimestamp.Add(record.Time.UtcTicks, sameTime);
                }
                sameTime.Add(index);
            }

            _duplicateGroups = MarkDuplicates();

            _grid.ClearSelection();
            _syncingSelection = false;
            _grid.ResumeLayout();

            // Records arrive newest first; show the sort glyph so that is obvious.
            if (_grid.Columns.Count > 0)
                _grid.Columns[0].HeaderCell.SortGlyphDirection = SortOrder.Descending;

            UpdateEnabledState();
        }

        /// <summary>Tints rows that share a timestamp, since they can only be deleted together.</summary>
        private int MarkDuplicates()
        {
            int groups = 0;
            foreach (KeyValuePair<long, List<int>> group in _rowsByTimestamp)
            {
                if (group.Value.Count < 2) continue;
                groups++;
                foreach (int index in group.Value)
                {
                    DataGridViewRow row = _grid.Rows[index];
                    row.DefaultCellStyle.BackColor = DuplicateBackColor;
                    row.Cells[0].ToolTipText = group.Value.Count +
                        " records share this exact timestamp. Deletion cannot pick between them, " +
                        "so they are selected and removed together.";
                }
            }
            return groups;
        }

        private static readonly Color DuplicateBackColor = Color.FromArgb(255, 246, 214);

        private static string FormatModTime(object modTime)
        {
            if (modTime is DateTimeOffset)
            {
                DateTimeOffset value = (DateTimeOffset)modTime;
                if (value == DateTimeOffset.MinValue) return "";
                return value.ToLocalTime().ToString(TimeFormats.Display);
            }
            if (modTime is DateTime)
            {
                DateTime value = (DateTime)modTime;
                if (value == DateTime.MinValue) return "";
                return value.ToLocalTime().ToString(TimeFormats.Display);
            }
            return "";
        }

        private List<HistoricRecord> SelectedRecords()
        {
            List<HistoricRecord> selected = new List<HistoricRecord>();
            foreach (DataGridViewRow row in _grid.SelectedRows)
            {
                HistoricRecord record = row.Tag as HistoricRecord;
                if (record != null) selected.Add(record);
            }
            selected.Sort(delegate(HistoricRecord a, HistoricRecord b) { return a.Time.CompareTo(b.Time); });
            return selected;
        }

        private void OnDelete(object sender, EventArgs e)
        {
            List<HistoricRecord> selected = SelectedRecords();
            if (selected.Count == 0 || _point == null) return;

            // A record whose exact timestamp could not be recovered must not be deleted: the only
            // timestamp available for it is the millisecond-rounded one, which addresses no record.
            List<HistoricRecord> deletable = new List<HistoricRecord>();
            int skipped = 0;
            foreach (HistoricRecord record in selected)
            {
                if (record.HasExactTime) deletable.Add(record);
                else skipped++;
            }
            if (skipped > 0)
            {
                string note = skipped + " of the selected records have no exact timestamp and will be skipped.";
                if (deletable.Count == 0)
                {
                    ShowWarning(note + Environment.NewLine + Environment.NewLine +
                                "There is nothing that can safely be deleted in this selection.");
                    return;
                }
                if (MessageBox.Show(this, note + Environment.NewLine + Environment.NewLine +
                        "Continue with the remaining " + deletable.Count + "?",
                        "Some records cannot be deleted",
                        MessageBoxButtons.OKCancel, MessageBoxIcon.Warning) != DialogResult.OK)
                    return;
            }
            selected = deletable;

            string aggregate = Convert.ToString(_aggregate.SelectedItem);
            bool dryRun = _dryRun.Checked;
            using (ConfirmDeleteForm confirm = new ConfirmDeleteForm(
                _point.FullName, aggregate, selected, dryRun))
            {
                if (confirm.ShowDialog(this) != DialogResult.OK) return;
            }

            if (dryRun)
            {
                ShowDryRunReport(selected);
                return;
            }

            // Snapshot how many records sit at each instant before anything is removed, so the
            // read-back can tell a partial duplicate-group deletion from a complete one.
            Dictionary<long, int> countsBefore = new Dictionary<long, int>();
            foreach (KeyValuePair<long, List<int>> entry in _rowsByTimestamp)
                countsBefore.Add(entry.Key, entry.Value.Count);

            List<DeleteFailure> failures;
            int attempted;
            bool cancelled;
            if (!RunDeletion(selected, out failures, out attempted, out cancelled))
                return;

            // Re-run the query so the grid shows what the server actually holds now, and use the
            // result to confirm the records really went. DeleteValue reports success even when the
            // timestamp matched nothing, so the read-back is the only trustworthy check.
            List<HistoricRecord> after = RunQuery();
            foreach (DeleteFailure survivor in FindSurvivors(selected, failures, after, countsBefore))
                failures.Add(survivor);

            ReportDeletion(attempted, selected.Count, failures, cancelled);
        }

        /// <summary>
        /// One entry per selected record, deliberately keeping repeats.
        ///
        /// Where several records share an instant, DeleteHistoricData removes one record per entry,
        /// not every record at that time: verified on the server, [t] left one of a duplicate pair
        /// standing while [t, t] cleared both. De-duplicating here would silently strand the
        /// survivors, so duplicates are selected together and passed through as they are.
        /// </summary>
        private static DateTimeOffset[] Timestamps(List<HistoricRecord> records)
        {
            DateTimeOffset[] times = new DateTimeOffset[records.Count];
            for (int i = 0; i < records.Count; i++) times[i] = records[i].Time;
            return times;
        }

        /// <summary>
        /// Records that were deleted without error but are still present afterwards. This is the
        /// silent no-op case, and it is a failure however cheerfully the server responded.
        /// </summary>
        private static List<DeleteFailure> FindSurvivors(List<HistoricRecord> attempted,
            List<DeleteFailure> alreadyFailed, List<HistoricRecord> after,
            Dictionary<long, int> countsBefore)
        {
            List<DeleteFailure> survivors = new List<DeleteFailure>();
            if (after == null) return survivors; // the re-query failed; nothing can be concluded

            // Counting rather than just checking presence, because several records can share an
            // instant: two of three going is a partial success, not a clean one.
            Dictionary<long, int> countsAfter = Tally(after);
            Dictionary<long, int> countsAttempted = Tally(attempted);

            Dictionary<long, bool> reported = new Dictionary<long, bool>();
            foreach (DeleteFailure failure in alreadyFailed) reported[failure.Record.Time.UtcTicks] = true;

            foreach (KeyValuePair<long, int> entry in countsAttempted)
            {
                if (reported.ContainsKey(entry.Key)) continue;

                int before = Count(countsBefore, entry.Key);
                int expected = Math.Max(0, before - entry.Value);
                int remaining = Count(countsAfter, entry.Key) - expected;
                if (remaining <= 0) continue;

                foreach (HistoricRecord record in attempted)
                {
                    if (record.Time.UtcTicks != entry.Key || remaining-- <= 0) continue;
                    DeleteFailure survivor = new DeleteFailure();
                    survivor.Record = record;
                    survivor.Message = "still present after the delete call reported success";
                    survivors.Add(survivor);
                }
            }
            return survivors;
        }

        private static Dictionary<long, int> Tally(List<HistoricRecord> records)
        {
            Dictionary<long, int> counts = new Dictionary<long, int>();
            foreach (HistoricRecord record in records)
                counts[record.Time.UtcTicks] = Count(counts, record.Time.UtcTicks) + 1;
            return counts;
        }

        private static int Count(Dictionary<long, int> counts, long key)
        {
            int value;
            return counts.TryGetValue(key, out value) ? value : 0;
        }

        /// <summary>Records sent to the server per call. Batching keeps large selections quick.</summary>
        private const int DeleteBatchSize = 200;

        /// <summary>Returns false when the worker itself blew up and nothing can be reported.</summary>
        private bool RunDeletion(List<HistoricRecord> records, out List<DeleteFailure> failures,
            out int attempted, out bool cancelled)
        {
            List<DeleteFailure> collected = new List<DeleteFailure>();
            int done = 0;
            string pointName = _point.FullName;

            using (ProgressForm progress = new ProgressForm("Deleting historic records", records.Count))
            {
                progress.Run(delegate(IProgressSink sink, CancellationToken cancellation)
                {
                    for (int offset = 0; offset < records.Count; offset += DeleteBatchSize)
                    {
                        if (cancellation.IsCancellationRequested) break;

                        int size = Math.Min(DeleteBatchSize, records.Count - offset);
                        List<HistoricRecord> batch = records.GetRange(offset, size);
                        sink.Report(offset, batch[0].LocalText);

                        try
                        {
                            _session.DeleteHistoricValues(pointName, Timestamps(batch));
                        }
                        catch (Exception)
                        {
                            // Retry the batch one at a time so the failure can be pinned on the
                            // record that actually caused it rather than all two hundred.
                            foreach (HistoricRecord record in batch)
                            {
                                try
                                {
                                    _session.DeleteHistoricValues(pointName,
                                        new DateTimeOffset[] { record.Time });
                                }
                                catch (Exception ex)
                                {
                                    DeleteFailure failure = new DeleteFailure();
                                    failure.Record = record;
                                    failure.Message = ex.Message;
                                    collected.Add(failure);
                                }
                            }
                        }
                        done += size;
                    }
                });
                progress.ShowDialog(this);

                failures = collected;
                attempted = done;
                cancelled = progress.WasCancelled;

                if (progress.Failure != null)
                {
                    ShowError("Deletion stopped unexpectedly.", progress.Failure);
                    return false;
                }
                return true;
            }
        }

        private void ReportDeletion(int attempted, int requested, List<DeleteFailure> failures, bool cancelled)
        {
            int deleted = attempted - failures.Count;
            string cancelNote = cancelled
                ? Environment.NewLine + "Cancelled after " + attempted + " of " + requested + " records."
                : "";
            if (failures.Count == 0)
            {
                MessageBox.Show(this,
                    deleted + " record" + (deleted == 1 ? "" : "s") +
                    " deleted, confirmed by re-reading the range." + cancelNote,
                    "Deletion complete", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            StringBuilder sb = new StringBuilder();
            sb.AppendLine(deleted + " of " + attempted + " records deleted. " +
                          failures.Count + " failed:" + cancelNote);
            sb.AppendLine();
            int shown = 0;
            foreach (DeleteFailure failure in failures)
            {
                if (shown++ >= 15) { sb.AppendLine("... and " + (failures.Count - 15) + " more."); break; }
                sb.AppendLine(failure.Record.LocalText + "  -  " + failure.Message);
            }
            MessageBox.Show(this, sb.ToString(), "Deletion finished with errors",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }

        private void ShowDryRunReport(List<HistoricRecord> records)
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("DRY RUN - nothing was deleted and no server call was made.");
            sb.AppendLine();
            sb.AppendLine("Would have called IHistory.DeleteHistoricData on");
            sb.AppendLine("    " + _point.FullName);
            sb.AppendLine("for the following " + records.Count + " timestamp" + (records.Count == 1 ? "" : "s") + ":");
            sb.AppendLine();
            int shown = 0;
            foreach (HistoricRecord record in records)
            {
                if (shown++ >= 25) { sb.AppendLine("    ... and " + (records.Count - 25) + " more."); break; }
                sb.AppendLine("    " + record.ExactLocalText + " local   (" + record.ExactUtcText + " UTC)");
            }
            MessageBox.Show(this, sb.ToString(), "Dry run", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        // ---------------------------------------------------------------- state

        private void UpdateConnectionStatus()
        {
            _connectionStatus.Text = "Connected to " + _session.Host + ":" + _session.Port +
                                     "  -  " + _session.ServerLabel +
                                     "  -  signed in as " + _session.UserName;
        }

        private void UpdateEnabledState()
        {
            int selected = _grid.SelectedRows.Count;
            _delete.Enabled = selected > 0 && _point != null;
            _delete.Text = selected > 0
                ? "Delete Selected (" + selected + ")..."
                : "Delete Selected...";
        }

        private void ShowWarning(string message)
        {
            MessageBox.Show(this, message, "Geo SCADA History Deleter",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }

        private void ShowError(string context, Exception ex)
        {
            MessageBox.Show(this, context + Environment.NewLine + Environment.NewLine + ex.Message,
                "Geo SCADA History Deleter", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }
}
