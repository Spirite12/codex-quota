# codex-quota

Codex 额度查询工具 / Read-only Codex quota monitor for Windows。

这是一个只读 Windows 额度查询工具，只显示当前已登录 Codex 账号返回的 5H 和 1W（周）额度，并以无边框小窗口贴近 Codex Desktop 底部。

## UI 样式

![额度胶囊 UI 示例](docs/ui-preview.png)

- **绿色**：剩余额度大于 60%
- **黄色**：剩余额度为 30%–60%
- **红色**：剩余额度低于 30%

如果账号没有 5H 限制，5H 胶囊会隐藏，1W 胶囊自动移动到原 5H 位置。

## 运行前提

- Windows x64。
- 需要安装 Microsoft .NET 10 Desktop Runtime。
- 如果需要生成安装包，则 `build-codex-quota.exe` 还需要本机安装 .NET SDK，因为它会调用 `dotnet` 生成安装包。

## 下载与安装

1. 安装 Microsoft .NET 10 Desktop Runtime。
2. 从 GitHub Releases 下载 `codex-quota.exe`。
3. 运行安装包并按提示选择安装路径。

普通用户不需要安装 .NET SDK；只有从源码构建安装包时才需要。

## 安全边界

- 仅在活动且可见的 ChatGPT Desktop 窗口、Codex 运行时和已登录账号同时存在时显示并读取；窗口关闭、最小化、切换应用或认证不可用时停止读取并隐藏，恢复后重试。
- 仅调用 `initialize`、`account/read`（`refreshToken: false`）和 `account/rateLimits/read`，并只接收 `account/rateLimits/updated` 通知。
- 保持只读：不登录、登出、切换账号、重置额度、创建对话或发送任务；不读取、复制或写入 Token、Cookie、`auth.json`。
- App Server 仅通过本地 `stdio` 子进程运行，不修改 Codex 安装目录。

## 刷新策略

- 启动且确认已登录后读取一次。
- 收到额度更新通知后立即重新读取一次。
- 仅在 Codex 仍打开时，以 120 秒作为兜底检查。

## 根目录程序

```text
codex-quota.exe             根目录引导器，启动 runtime/codex-quota.exe
codex-quota-launcher.exe    启动监听器，绑定 Codex 启动任务
uninstall-codex-quota.exe   卸载任务、注册信息和安装目录
build-codex-quota.exe       构建 D:\codex-quota.exe 安装包
```
