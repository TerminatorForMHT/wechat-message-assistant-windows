
## 【自用工具分享】把微信新消息转到 Windows 系统 Toast 通知，带联系人头像，不吵

日常微信消息太多，总不能一直挂着微信等。写了个小工具 **WxToastNotifier**，把微信新消息直接推到系统级 Toast 通知上，跟 QQ/邮件那种一个德行。

**GitHub 项目**：https://github.com/TerminatorForMHT/wechat-message-assistant-windows
**直接下载**：https://github.com/TerminatorForMHT/wechat-message-assistant-windows/releases

### 它能干什么

- 微信来新消息 → 系统右下角弹个 Toast，带 **联系人圆形头像**、标题和消息正文
- **自己发的消息不会被推**（不会来回收到自己发的"晚安"）
- **免打扰会话自动跳过**
- 微信打开时同步的历史消息不会刷屏（有时间门限）
- **公众号文章、订阅号推送一律不推**（它们走 `brandsessionholder` 聚合会话，工具直接过滤掉）
- 托盘常驻，双击恢复，右键退出
- 单实例，开俩窗口不会推两遍

### 关键：微信 4.1 / 4.2 怎么解密的

从微信自己的进程内存里扫 `Config.Cipher` 全局对象，直接拿到每个数据库的 `(salt, enc_key)`。**不用 passphrase，不用 all_keys.json，不用 root**。微信自己的内存里就有密钥，只不过没人去读。

解密后的副本写到临时目录，**原始 `db_storage` 一行不会被碰**。

### 安装

下载 Release 里的 `.msix` 直接双击。如果提示证书不受信任，先导入一下：

```powershell
Import-Certificate .\WxToastNotifier.cer -CertStoreLocation Cert:\LocalMachine\Root
Import-Certificate .\WxToastNotifier.cer -CertStoreLocation Cert:\LocalMachine\TrustedPeople
```

或直接 `Install.ps1`。

### 使用

1. 先开微信（必须，工具要扫微信进程拿密钥）
2. 运行 WxToastNotifier
3. 点"开始监听" → 自动扫 `db_storage`、解密、建基线
4. 完事，放托盘就行

### 技术栈

WinUI 3 + Windows App SDK + H.NotifyIcon + SQLitePCLRaw。跑在 .NET 9 上，打包成 MSIX 安装。

### 已知限制

- 只测了微信 4.1 / 4.2 Windows 版，其他版本 WCDB 密钥位置可能变了，微信更新后如果拿不到密钥会在日志里写，到时候提 Issue 一起看
- 企业微信 / 多账号未测试
- 头像用 contact 表里的 `small_head_url` 直接下，腾讯 CDN 偶尔会慢一丢丢，但后台并发下载不影响推送速度

欢迎提 Issue 或者 PR，尤其是数据库 schema 变化的反馈。有啥想法也可以在项目里讨论。

**GitHub**：https://github.com/TerminatorForMHT/wechat-message-assistant-windows
**Releases（下载 MSIX）**：https://github.com/TerminatorForMHT/wechat-message-assistant-windows/releases

（附上你自己的截图：主窗口界面一张，通知中心带头像的 Toast 一张，托盘菜单一张）
