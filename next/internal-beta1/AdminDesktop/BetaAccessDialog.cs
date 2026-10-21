using System.Net.Http.Json;
using SmbManager.Local;

namespace SmbManager.Local.AdminDesktop;

internal sealed class BetaAccessDialog : Form
{
    public BetaAccessDialog(HttpClient http, string id)
    {
        Text = "시험 채널 접근 자격"; UiTheme.Form(this, new(650, 440), new(600, 400));
        var root = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new(18), ColumnCount = 1, RowCount = 3 };
        root.RowStyles.Add(new(SizeType.Percent, 100)); root.RowStyles.Add(new(SizeType.AutoSize)); root.RowStyles.Add(new(SizeType.AutoSize)); Controls.Add(root);
        var info = new TextBox { Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical };
        var administrator = new CheckBox { Text = "운영부 외 PC에 클로즈베타 시험 자격 부여", AutoSize = true };
        var save = new Button { Text = "저장" }; UiTheme.Button(save, true);
        root.Controls.Add(info, 0, 0); root.Controls.Add(administrator, 0, 1); root.Controls.Add(save, 0, 2); save.Enabled = false;
        Shown += async (_, _) =>
        {
            try
            {
                var access = await http.GetFromJsonAsync<BetaAccess>($"api/admin/devices/{id}/beta", Wire.Json) ?? throw new InvalidDataException();
                if (IsDisposed) return;
                administrator.Checked = access.Administrator; save.Enabled = true;
                info.Text = $"서버 배정 부서: {access.Department}\r\n허용 채널: {string.Join(" / ", access.Allowed.Select(BetaChannels.Label))}\r\n\r\n운영부는 PC 부서·권한 관리의 부서 배정으로 판정합니다.\r\n시험 자격은 이 화면에서 별도로 부여·회수합니다. 서버관리 권한은 부여하지 않습니다.\r\nPC 이름이나 Windows 사용자 이름은 자격 판정에 쓰지 않습니다.\r\n\r\n승인된 운영부·관리자만 클로즈베타에 접근합니다.\r\n시험 자격은 Drive 계정·공유폴더·서버관리 권한을 바꾸지 않습니다.";
            }
            catch (Exception e) { if (!IsDisposed) info.Text = e.Message; }
        };
        save.Click += async (_, _) =>
        {
            save.Enabled = false;
            try
            {
                if (administrator.Checked && MessageBox.Show(this, "선택 PC를 클로즈베타 시험 대상에 포함할까요?\n이 PC에 클로즈베타 접근 자격을 부여합니다.", "시험 자격 확인", MessageBoxButtons.YesNo) != DialogResult.Yes) return;
                using var response = await http.PostAsJsonAsync($"api/admin/devices/{id}/beta-administrator", new BetaAdministrator(administrator.Checked), Wire.Json);
                response.EnsureSuccessStatusCode(); DialogResult = DialogResult.OK; Close();
            }
            catch (Exception e) { if (!IsDisposed) info.Text = e.Message; }
            finally { if (!IsDisposed) save.Enabled = true; }
        };
    }
}
