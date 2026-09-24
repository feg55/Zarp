using System;
using System.Drawing;
using System.Windows.Forms;
using Zarp.Core;

namespace Zarp.UI
{
    sealed class CloseActionForm : Form
    {
        readonly CheckBox _remember = new CheckBox();

        public bool MinimizeToTray { get; private set; }
        public bool RememberChoice => _remember.Checked;

        public CloseActionForm(bool minimizeToTray, bool disconnectOnExit)
        {
            Text = L.T("close.caption");
            AutoScaleMode = AutoScaleMode.Dpi;
            AutoScaleDimensions = new SizeF(96f, 96f);
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.CenterParent;
            BackColor = Theme.Back;
            ForeColor = Theme.Text;
            Font = Theme.Font(9.5f);
            Theme.DarkTitleBar(this);

            const int Pad = 20;
            // Имена кнопок не зависят от языка: по ним их находят тесты и средства доступности.
            var tray = Theme.FlatButton(L.T("close.toTray"), 184, primary: minimizeToTray);
            tray.Name = "tray";
            tray.Click += (s, e) => SelectAction(true);
            var exit = Theme.FlatButton(L.T("close.exit"), 208, primary: !minimizeToTray);
            exit.Name = "exit";
            exit.Click += (s, e) => SelectAction(false);
            AcceptButton = minimizeToTray ? tray : exit;

            // Ширина по кнопкам, высота по тексту: переводы бывают длиннее русского.
            int width = Math.Max(400, tray.Width + 8 + exit.Width);
            var title = Theme.WrappedLabel(L.T("close.question"), Theme.Font(12f, FontStyle.Bold), Theme.Text, new Point(Pad, 18), width);
            var description = Theme.WrappedLabel(
                L.T("close.trayInfo") + "\n" + L.T(disconnectOnExit ? "close.exitDisconnects" : "close.exitKeeps"),
                Font, Theme.TextDim, new Point(Pad, title.Bottom + 10), width);
            _remember.Text = L.T("close.remember");
            _remember.Font = Font;
            _remember.Location = new Point(Pad, description.Bottom + 10);
            _remember.Size = _remember.GetPreferredSize(Size.Empty);
            var hint = Theme.WrappedLabel(L.T("close.hint"), Font, Theme.TextDim, new Point(Pad, _remember.Bottom + 8), width);
            tray.Location = new Point(Pad, hint.Bottom + 16);
            exit.Location = new Point(Pad + width - exit.Width, tray.Top);

            ClientSize = new Size(width + Pad * 2, tray.Bottom + 18);
            Controls.AddRange(new Control[] { title, description, _remember, hint, tray, exit });
        }

        void SelectAction(bool minimizeToTray)
        {
            MinimizeToTray = minimizeToTray;
            DialogResult = DialogResult.OK;
        }

        protected override bool ProcessDialogKey(Keys keyData)
        {
            if (keyData == Keys.Escape)
            {
                DialogResult = DialogResult.Cancel;
                return true;
            }
            return base.ProcessDialogKey(keyData);
        }
    }
}
