using System.Drawing;
using System.Windows.Forms;

namespace Zarp.UI
{
    sealed class CloseActionForm : Form
    {
        readonly CheckBox _remember = new CheckBox();

        public bool MinimizeToTray { get; private set; }
        public bool RememberChoice => _remember.Checked;

        public CloseActionForm(bool minimizeToTray, bool disconnectOnExit)
        {
            Text = "Закрытие Zarp";
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
            ClientSize = new Size(440, 224);
            Theme.DarkTitleBar(this);

            var title = new Label
            {
                Text = "Что сделать при закрытии окна?",
                Font = Theme.Font(12f, FontStyle.Bold),
                AutoSize = true, Location = new Point(20, 18),
            };
            var description = new Label
            {
                Text = "В трее Zarp продолжит работать в фоне.\n" +
                    (disconnectOnExit ? "При выходе WARP будет отключён." : "При выходе WARP останется подключённым."),
                ForeColor = Theme.TextDim, Location = new Point(20, 54), Size = new Size(400, 46),
            };
            _remember.Text = "Запомнить мой выбор";
            _remember.AutoSize = true;
            _remember.Location = new Point(20, 108);
            var hint = new Label
            {
                Text = "Выбор можно изменить в настройках.",
                ForeColor = Theme.TextDim, AutoSize = true, Location = new Point(20, 138),
            };

            var tray = Theme.FlatButton("Скрыть в трей", 184, primary: minimizeToTray);
            tray.Location = new Point(20, 174);
            tray.Click += (s, e) => SelectAction(true);
            var exit = Theme.FlatButton("Закрыть приложение", 208, primary: !minimizeToTray);
            exit.Location = new Point(212, 174);
            exit.Click += (s, e) => SelectAction(false);
            AcceptButton = minimizeToTray ? tray : exit;

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
