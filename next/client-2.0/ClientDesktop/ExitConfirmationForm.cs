using System.Globalization;
using System.Security.Cryptography;
using SmbManager.Local;

namespace SmbManager.Local.ClientDesktop;

// Local intent confirmation, not a network CAPTCHA or an authorization boundary.
internal sealed class ExitConfirmationForm : Form
{
    readonly Label code = new() { AutoSize = true, Font = UiTheme.Font(28, true), AccessibleName = "종료 확인코드", Margin = new(0, 8, 0, 8) };
    readonly TextBox input = new() { Dock = DockStyle.Fill, MaxLength = 4, Font = UiTheme.Font(16), AccessibleName = "위에 표시된 4자리 확인코드 입력", ImeMode = ImeMode.Disable };
    readonly Label error = new() { AutoSize = true, ForeColor = Color.FromArgb(160, 30, 30), Margin = new(0, 6, 0, 6) };
    string expected = "";

    public ExitConfirmationForm()
    {
        Text = "앱 완전히 종료";
        UiTheme.Form(this, new(520, 440), new(420, 380));
        StartPosition = FormStartPosition.CenterParent;
        MinimizeBox = MaximizeBox = false;
        ShowInTaskbar = false;
        var viewport = new Panel { Dock = DockStyle.Fill, AutoScroll = true, Padding = new(20) };
        var root = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 1 };
        Controls.Add(viewport); viewport.Controls.Add(root);
        var explanation = new Label { Text = "종료하면 자동 연결과 회사 알림 수신이 멈춥니다.\n계속 사용하려면 ‘계속 사용’을 누르세요.\n종료하려면 아래 확인코드 4자리를 입력하세요.", AutoSize = true, Margin = new(0, 0, 0, 6) };
        root.Controls.Add(explanation); UiTheme.Wrap(explanation, root, 8);
        root.Controls.Add(code);
        root.Controls.Add(new Label { Text = "확인코드 입력", AutoSize = true, Margin = new(0, 4, 0, 6) });
        root.Controls.Add(input);
        root.Controls.Add(error); UiTheme.Wrap(error, root, 8);
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, WrapContents = true, Margin = new(0, 8, 0, 0) };
        var cancel = ClientForm.ActionButton("계속 사용", true);
        var confirm = ClientForm.ActionButton("확인 후 종료");
        cancel.DialogResult = DialogResult.Cancel; CancelButton = cancel;
        buttons.Controls.Add(cancel); buttons.Controls.Add(confirm); root.Controls.Add(buttons);
        AcceptButton = confirm;
        confirm.Click += (_, _) => {
            if(input.Text.Length == 4 && string.Equals(input.Text, expected, StringComparison.Ordinal)) {
                DialogResult = DialogResult.OK;
                Close();
                return;
            }
            NewCode();
            error.Text = "코드가 일치하지 않습니다. 새로 표시된 4자리를 입력하세요.";
            input.Clear(); input.Focus();
        };
        NewCode();
        Shown += (_, _) => input.Focus();
    }

    void NewCode()
    {
        var previous = expected;
        do { expected = RandomNumberGenerator.GetInt32(10000).ToString("D4", CultureInfo.InvariantCulture); }
        while(expected == previous);
        code.Text = expected;
    }
}
