using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Win32;

namespace Perch.Windows;

public sealed partial class PanelModel
{
    public DshService Dsh { get; }
    private Account? editingDsh;
    private int dshMode = 1;
    private string dshPath = "", dshNode = "", dshHome = "", dshProfile = "web", dshUrl = "", dshBalanceHome = "";
    private void InitializeDshEditor()
    {
        dshPath = Demo ? @"C:\Tools\deepseek-harness" : DshInstallation.FindNpm() ?? "";
        dshNode = Demo ? @"C:\Program Files\nodejs\node.exe" : DshInstallation.FindNode() ?? "";
    }
    public bool NewDsh { get => NewKind == 2; set { if (value) NewKind = 2; } }
    public bool NewNative => !NewDsh;
    public string CreateLabel => NewDsh ? editingDsh != null ? "保存配置" : DshExternal ? "连接并添加" : "创建并启动" : Strings.Get("account.create");
    public int DshMode
    {
        get => dshMode;
        set
        {
            if (!Set(ref dshMode, value)) return;
            DshPath = Demo ? @"C:\Tools\deepseek-harness" : value == 1 ? DshInstallation.FindNpm() ?? "" : "";
            Changed(nameof(DshExternal));
            Changed(nameof(DshManaged));
            Changed(nameof(DshInstallHint));
            Changed(nameof(CreateLabel));
        }
    }
    public bool DshExternal => DshMode == 2;
    public bool DshManaged => !DshExternal;
    public string DshInstallHint => DshMode == 0 ? "源码仓库根目录（先完成 pnpm run build）" : "npm 包目录或含本地安装的项目目录";
    public string DshPath { get => dshPath; set => Set(ref dshPath, value); }
    public string DshNode { get => dshNode; set => Set(ref dshNode, value); }
    public string DshHome { get => dshHome; set => Set(ref dshHome, value); }
    public string DshProfile { get => dshProfile; set => Set(ref dshProfile, value); }
    public string DshUrl { get => dshUrl; set => Set(ref dshUrl, value); }
    public string DshBalanceHome { get => dshBalanceHome; set => Set(ref dshBalanceHome, value); }
    public Command ChooseDshBalanceHome => new(() => { var picker = new OpenFolderDialog { Title = "选择已有实例的 DSH_HOME（只读 API Key 查询余额）" }; if (picker.ShowDialog() == true) DshBalanceHome = picker.FolderName; });
    public Command ChooseDshInstallation => new(() => { var picker = new OpenFolderDialog { Title = DshInstallHint }; if (picker.ShowDialog() == true) DshPath = picker.FolderName; });
    public Command ChooseDshHome => new(() => { var picker = new OpenFolderDialog { Title = "选择已有 DSH_HOME（不复制登录信息）" }; if (picker.ShowDialog() == true) DshHome = picker.FolderName; });
    public Command ChooseDshNode => new(() => { var picker = new OpenFileDialog { Title = "选择 Node.js", Filter = "Node.js|node.exe" }; if (picker.ShowDialog() == true) DshNode = picker.FileName; });
    public Command DetectDsh => new(() => { DshMode = 1; InitializeDshEditor(); Changed(nameof(DshPath)); Changed(nameof(DshNode)); if (DshPath.Length == 0) Notify("未发现 npm 安装的 dsh，请手动选择安装目录。"); });

    public async Task OpenClient(Account account)
    {
        if (Demo) { Notify("演示模式，不打开客户端。"); return; }
        if (account.App != "dsh") { await Clients.Open(account); return; }
        try
        {
            var url = await Dsh.Ensure(account);
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true })?.Dispose();
        }
        finally
        {
            snapshots[account.Id] = await Dsh.Read(account);
            MergeAccounts();
        }
    }

    private async Task CreateDsh()
    {
        var account = editingDsh ?? new Account { App = "dsh", Name = NewName.Trim() };
        if (editingDsh != null && (DshState.ChildAlive(Dsh.State(account).Read()) || Dsh.State(account).HostAlive()))
            throw new IOException("请先停止这个 dsh 服务，再修改启动配置；可用“重新连接”更新授权。");
        var home = DshExternal ? account.ProfilePath.Length > 0 && account.Dsh?.Mode == "external" ? account.ProfilePath : Path.Combine(Store.Root, "dsh-external", account.Id)
            : string.IsNullOrWhiteSpace(DshHome) ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".perch", "profiles", Guid.NewGuid().ToString("N")[..10], "dsh-home") : Path.GetFullPath(DshHome.Trim());
        if (Store.Accounts.Any(a => a.Id != account.Id && a.App == "dsh" && Store.SamePath(a.ProfilePath, home)))
            throw new IOException("这个 DSH_HOME 已被另一个账号引用，请使用已有记录。");
        var config = new DshConfiguration { Mode = DshExternal ? "external" : DshMode == 0 ? "source" : "npm", InstallationPath = DshExternal ? "" : DshPath.Trim(), NodePath = DshExternal ? "" : DshNode.Trim(), Profile = DshProfile.Trim(),
            BalanceHome = DshExternal && !string.IsNullOrWhiteSpace(DshBalanceHome) ? Path.GetFullPath(DshBalanceHome.Trim()) : "" };
        // Validate a detached value so failed configuration never mutates a saved account.
        var candidate = new Account { Id = account.Id, App = "dsh", Name = NewName.Trim(), ProfilePath = home, SessionRoot = home, Dsh = config };
        if (DshExternal)
        {
            // Editing only the balance directory must not require another token or interrupt dsh.
            if (editingDsh?.Dsh?.Mode != "external" || !string.IsNullOrWhiteSpace(DshUrl)) await Dsh.Connect(candidate, DshUrl);
        }
        else
            DshInstallation.Resolve(candidate);
        Store.PrivateDirectory(home);
        account.Name = candidate.Name;
        account.ProfilePath = home;
        account.SessionRoot = home;
        account.Dsh = config;
        if (editingDsh == null) Store.Add(account); else Store.Save();
        editingDsh = null;
        DshUrl = "";
        DshBalanceHome = "";
        NewName = "";
        Page = "accounts";
        Quotas.Clear(account.Id);
        MergeAccounts();
        await OpenClient(account);
        await Refresh(account);
        Changed(nameof(CreateLabel));
    }

    public void ConfigureDsh(Account account)
    {
        editingDsh = account;
        NewKind = 2;
        NewName = account.Name;
        DshMode = account.Dsh?.Mode switch { "source" => 0, "external" => 2, _ => 1 };
        DshPath = account.Dsh?.InstallationPath ?? "";
        DshNode = account.Dsh?.NodePath ?? "";
        DshProfile = account.Dsh?.Profile ?? "web";
        DshHome = account.Dsh?.Mode == "external" ? "" : account.ProfilePath;
        DshUrl = "";
        DshBalanceHome = account.Dsh?.BalanceHome ?? "";
        Page = "add";
        Changed(nameof(CreateLabel));
    }

    private void ResetDshEditor()
    {
        editingDsh = null;
        DshUrl = "";
        DshHome = "";
        DshBalanceHome = "";
        Changed(nameof(CreateLabel));
    }

    public async Task ReconnectDsh(Account account)
    {
        if (Demo) { Notify("演示模式，不连接真实实例。"); return; }
        var url = PromptConnection?.Invoke("粘贴 dsh 当前启动的本机授权 URL", "");
        if (string.IsNullOrWhiteSpace(url)) return;
        await Dsh.Connect(account, url);
        Quotas.Clear(account.Id);
        await Refresh(account);
    }

    public async Task StopDsh(Account account)
    {
        if (Demo) { Notify("演示模式，不停止真实服务。"); return; }
        var external = account.Dsh?.Mode == "external";
        if (!external && Confirm?.Invoke("停止这个 dsh 服务？正在运行的任务会被中断。数据目录将保留。") != true) return;
        await Dsh.Stop(account);
        Quotas.Clear(account.Id);
        snapshots[account.Id] = await Dsh.Read(account);
        MergeAccounts();
    }

}
