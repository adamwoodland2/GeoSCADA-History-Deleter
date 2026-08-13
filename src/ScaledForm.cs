using System.Drawing;
using System.Windows.Forms;

namespace HistoryDeleter
{
    /// <summary>
    /// Base for every window in the tool. The layouts are hand-coded in 96 DPI pixels, so each form
    /// declares the font metrics it was laid out against and lets WinForms scale the child
    /// coordinates when the machine runs at 125%, 150%, 200% and so on.
    /// </summary>
    internal class ScaledForm : Form
    {
        /// <summary>Segoe UI 9pt at 96 DPI, which is what the coordinates in these forms assume.</summary>
        private static readonly SizeF DesignDimensions = new SizeF(7F, 15F);

        private float _scale = 1f;

        protected ScaledForm()
        {
            Font = SystemFonts.MessageBoxFont;
        }

        /// <summary>
        /// Must be the last thing every derived constructor does. Auto-scaling is applied at the
        /// moment AutoScaleMode is assigned, so setting it before the controls exist scales nothing
        /// and leaves a 96 DPI layout being drawn with high-DPI fonts.
        /// </summary>
        protected void ApplyScaling()
        {
            SuspendLayout();
            AutoScaleDimensions = DesignDimensions;
            AutoScaleMode = AutoScaleMode.Font;
            ResumeLayout(false);
            PerformLayout();

            // Only meaningful once AutoScaleMode is Font; before that it reports the unset value.
            SizeF current = CurrentAutoScaleDimensions;
            _scale = current.Width > 0 ? current.Width / DesignDimensions.Width : 1f;
        }

        /// <summary>
        /// Converts a 96 DPI design measurement to device pixels. Needed for the few things WinForms
        /// auto-scaling ignores, notably DataGridView and ListView column widths.
        /// </summary>
        protected int Scaled(int designPixels)
        {
            return (int)System.Math.Round(designPixels * _scale);
        }

        /// <summary>A caption label that ellipsises rather than silently clipping mid-glyph.</summary>
        protected static Label Caption(string text, int left, int top, int width)
        {
            Label label = new Label();
            label.AutoSize = false;
            label.Text = text;
            label.SetBounds(left, top, width, 20);
            label.TextAlign = ContentAlignment.MiddleLeft;
            label.AutoEllipsis = true;
            return label;
        }
    }
}
