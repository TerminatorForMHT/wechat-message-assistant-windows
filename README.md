# 微信消息助手（WxToastNotifier）

非侵入地将微信（Windows 版 4.1+）中未被免打扰的新消息，转发到 Windows 系统 Toast 通知中心。项目已由最初的 Python 版本**整体重写为 C# + WinUI 3**，以 MSIX 打包分发。

> 仅读取本地微信数据库做轮询，**不注入、不修改微信进程与文件**；解密输出全部写入系统临时目录，绝不改动微信原始数据。

## 功能特性

- **免打扰过滤**：群聊看 `contact.chat_room_notify`，单聊看 `contact.flag` 第 9 位（`0x200`），两者任一生效即静音。
- **过滤自己发出的消息**：按 `message_0.db` 的 `real_sender_id == 2` 判定，避免自己被推送打断。
- **密钥自动提取**：基于 WCDB `Config.Cipher` 配置对象定位 + HMAC 校验（微信 4.1.15+），无需注入、无封号风险。
- **数据库解密**：SQLCipher 解密 `session.db` / `contact.db` / `message_0.db` 到临时目录后只读分析。
- **接近实时的推送**：30ms 轻量轮询检测 db 文件变化，变了才解密，追平原 Python 版的响应速度。
- **多账户自动识别**：自动选择写入时间最新的数据库作为当前登录账户。
- **当前账户显示微信号**：优先 `contact.alias`（微信号），为空则回退昵称。
- **微信拉起**：启动时检测微信是否运行，未运行则显示「打开微信」按钮。
- **最小化到托盘**：关闭窗口即隐藏到托盘，右键菜单为 WinUI 3 原生样式。

## 环境要求

- Windows 10 1809（`10.0.17763`）及以上，或 Windows 11
- 微信 for Windows **4.1+**（登录且已运行）
- 构建需要 .NET 9 SDK + Windows App SDK

## 安装（二进制）

1. 以**管理员**身份打开 PowerShell，把自签名根证书加入「本地计算机」受信任根（否则报 `0x800B0109`）：

   ```powershell
   certutil -addstore -f Root .\WxToastNotifier.cer
   ```

2. 安装 MSIX 包：

   ```powershell
   Add-AppxPackage -Path .\WxToastNotifier_2.0.0.0_x64.msix
   ```

3. 从「开始」菜单打开「WxToastNotifier」。

> 证书仅用于给 MSIX 签名，`.cer` 为公钥（随包分发）；`.pfx` 含私钥，仅构建机保留，请勿分发或提交。

## 使用

1. 打开应用，确认界面已自动检测到 `db_storage` 目录（未检测到则手动填写）。
2. 微信已在运行并登录的情况下，点「开始监听」。
3. 之后他人发来未处于免打扰会话的消息，右下角会弹出系统通知。

配置项（保存于 `%LocalAppData%\WxToastNotifier\config.json`）：

- `DbStorage`：微信 `db_storage` 目录
- `Passphrase`：可选，微信 4.1+ 的数据库 passphrase（一般无需填写，密钥自动提取）
- `PollIntervalSeconds`：轮询兜底间隔（实际推送由 30ms 文件变化检测驱动）

## 工作原理

1. **密钥提取**：从微信进程内存定位 WCDB `Config.Cipher` 锚点，解码 + HMAC 校验得到每个数据库的密钥。
2. **变化检测**：每 30ms 比对 `session.db` / `contact.db`（含 `-wal`）的大小与修改时间，绝不解密。
3. **解密与读取**：检测到变化后，SQLCipher 解密到临时目录并读取会话/联系人，依据免打扰与「自己消息」过滤。
4. **通知**：通过 `Windows.UI.Notifications.ToastNotificationManager`（WinRT，MSIX 下无需额外 COM 注册）弹出系统 Toast。

## 项目结构

```
WxToastNotifier/
├── App.xaml(.cs)              应用入口与生命周期
├── MainWindow.xaml(.cs)       主窗口 + 托盘实现（H.NotifyIcon.WinUI）
├── MainPage.xaml(.cs)         设置/状态/日志界面
├── Package.appxmanifest       MSIX 清单
├── WxToastNotifier.csproj     项目与打包配置
├── Data/WeChatDatabase.cs     数据库解密与读取、免打扰判定
├── Models/                    AppConfig、SessionRecord 等模型
├── Security/                  密钥提取（WeChatKeyExtractor）、解密（WeChatCrypto）、密钥提供（DbKeyProvider）
└── Services/                  MessageMonitor（30ms 检测）、ToastService、WeChatProcessService
```

## 构建

```powershell
# 生成 MSIX 安装包（Release / x64，需先配置 PackageCertificateKeyFile 或改为 Thumbprint）
dotnet build .\WxToastNotifier\WxToastNotifier.csproj -c Release -p:Platform=x64 -p:GenerateAppxPackageOnBuild=true

# 产物位于
# WxToastNotifier\AppPackages\WxToastNotifier_2.0.0.0_x64_Test\WxToastNotifier_2.0.0.0_x64.msix
```

## 免责声明

本工具仅用于个人效率用途，读取本地数据库做消息提醒，不注入、不篡改微信客户端。请遵守微信相关服务条款，开发者不对使用结果承担任何责任。

## License

见 [LICENSE](LICENSE)。