using System;
using System.Drawing;
using System.Windows.Forms;

namespace HistoryDeleter
{
    /// <summary>
    /// Shown before anything else. The tool destroys historian data irreversibly, so the operator
    /// has to accept the terms before a connection is even offered.
    /// </summary>
    internal sealed class DisclaimerForm : ScaledForm
    {
        private const string Text_ =
@"------------------------------------------------------------------------------
 Geo SCADA History Deleter   Copyright (C) 2026  Adam Woodland
 Licensed under the MIT Licence. This is free software, and you are welcome to
 redistribute it under those conditions.

 DISCLAIMER
 This tool is provided ""AS IS"", WITHOUT WARRANTY OF ANY KIND, express or
 implied. The author accepts no liability for any damages arising from its use.
 It PERMANENTLY DELETES records from the Geo SCADA historian. Deletions cannot
 be undone, and the tool is NOT certified for production SCADA systems. Do NOT
 run it against a production or safety-critical system without first reviewing
 the code and testing on a representative non-production system. You run it at
 your own risk and remain responsible for your own change-control, record
 retention and security policies.
------------------------------------------------------------------------------";

        public DisclaimerForm()
        {
            Text = "Geo SCADA History Deleter - disclaimer";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            StartPosition = FormStartPosition.CenterScreen;
            MaximizeBox = false;
            MinimizeBox = false;
            ClientSize = new Size(640, 292);

            TextBox body = new TextBox();
            body.SetBounds(12, 12, 616, 202);
            body.Multiline = true;
            body.ReadOnly = true;
            body.WordWrap = false;
            body.ScrollBars = ScrollBars.Vertical;
            body.BackColor = SystemColors.Window;
            body.Font = new Font(FontFamily.GenericMonospace, 8.25f);
            body.Text = Text_.Replace("\n", Environment.NewLine);
            body.Select(0, 0);

            Label prompt = new Label();
            prompt.SetBounds(12, 222, 616, 20);
            prompt.Text = "Do you accept these terms?";

            Button accept = new Button();
            accept.SetBounds(140, 250, 348, 30);
            accept.Text = "&Yes - I accept the terms and have tested appropriately";
            accept.DialogResult = DialogResult.Yes;

            Button decline = new Button();
            decline.SetBounds(500, 250, 128, 30);
            decline.Text = "&No - do not run";
            decline.DialogResult = DialogResult.No;

            Controls.AddRange(new Control[] { body, prompt, accept, decline });
            // Declining is the safe default, so that is what Enter and Escape both do.
            AcceptButton = decline;
            CancelButton = decline;
            ActiveControl = decline;
            ApplyScaling();
        }
    }
}
