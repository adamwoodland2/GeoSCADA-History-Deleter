using System;
using System.Drawing;
using System.Threading;
using System.Windows.Forms;

namespace HistoryDeleter
{
    internal interface IProgressSink
    {
        void Report(int completed, string detail);
    }

    /// <summary>
    /// Runs a long server operation on a worker thread with a cancellable progress dialog, so the
    /// window keeps painting while a large selection is deleted one record at a time.
    /// </summary>
    internal sealed class ProgressForm : ScaledForm, IProgressSink
    {
        private readonly ProgressBar _bar = new ProgressBar();
        private readonly Label _detail = new Label();
        private readonly Button _cancel = new Button();
        private readonly CancellationTokenSource _cancellation = new CancellationTokenSource();
        private readonly int _total;

        private Action<IProgressSink, CancellationToken> _work;
        private Exception _failure;

        public ProgressForm(string caption, int total)
        {
            _total = total;

            Text = caption;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            StartPosition = FormStartPosition.CenterParent;
            MaximizeBox = false;
            MinimizeBox = false;
            ControlBox = false;
            ClientSize = new Size(420, 128);

            _bar.SetBounds(16, 20, 388, 22);
            _bar.Minimum = 0;
            _bar.Maximum = Math.Max(1, total);

            _detail.SetBounds(16, 52, 388, 20);
            _detail.ForeColor = SystemColors.GrayText;

            _cancel.SetBounds(316, 84, 88, 27);
            _cancel.Text = "Cancel";
            _cancel.Click += delegate
            {
                _cancel.Enabled = false;
                _detail.Text = "Finishing the record in flight...";
                _cancellation.Cancel();
            };

            Controls.AddRange(new Control[] { _bar, _detail, _cancel });
            ApplyScaling();
        }

        public void Run(Action<IProgressSink, CancellationToken> work)
        {
            _work = work;
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            if (_work == null) return;

            Thread worker = new Thread(delegate()
            {
                try { _work(this, _cancellation.Token); }
                catch (Exception ex) { _failure = ex; }
                BeginInvoke((MethodInvoker)delegate { Close(); });
            });
            worker.IsBackground = true;
            worker.Start();
        }

        void IProgressSink.Report(int completed, string detail)
        {
            if (!IsHandleCreated) return;
            BeginInvoke((MethodInvoker)delegate
            {
                if (IsDisposed) return;
                _bar.Value = Math.Min(_bar.Maximum, completed);
                _detail.Text = "Record " + (completed + 1) + " of " + _total + "   " + detail;
            });
        }

        /// <summary>Set when the worker itself threw, as opposed to an individual record failing.</summary>
        public Exception Failure { get { return _failure; } }

        public bool WasCancelled { get { return _cancellation.IsCancellationRequested; } }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            base.OnFormClosed(e);
            _cancellation.Dispose();
        }
    }
}
