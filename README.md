# ModTranslater(CN)

将 Penumbra 已安装 Mod 的英文选项和说明翻译为简体中文。

支持 OpenAI 兼容接口、百度翻译、腾讯翻译、有道翻译和 Google Cloud 翻译。API 凭证在本地加密保存。

## 安装

在卫月设置的“实验性 → 自定义插件仓库”中添加以下链接并保存，然后在插件安装器中搜索 `ModTranslater(CN)` 并安装：

```text
https://raw.githubusercontent.com/MomijiOXO/FF14_ModTranslater/main/repo.json
```

也可从 [Releases](https://github.com/MomijiOXO/FF14_ModTranslater/releases) 下载 ZIP，解压后在卫月开发插件设置中添加 `ModTranslater.dll`。切换安装方式前，请先停用原来的开发插件副本。

## 使用方法

1. 启用 Penumbra，输入 `/modtrans` 或点击插件的窗口按钮。
2. 在“翻译服务”页配置 API 并保存。OpenAI 兼容接口还需要填写接口地址和模型名称。
3. 在“模组汉化”页点击“扫描新增与更新”，或勾选多个 Mod 后点击“强制汉化所选”。

插件自动读取 Penumbra 的 Mod 目录，无需手动选择文件夹。

- 普通扫描：处理新增、更新或上次失败的 Mod；展示文本中含中文则跳过整个 Mod。
- 强制汉化：只翻译不含中文的英文条目，保留中文和中英混合内容。
- 修改前自动备份，可在窗口内恢复所选 Mod 的备份。

## 编译

需要 .NET 10 SDK 和对应版本的卫月程序集（当前为 API 15）。在项目目录运行：

```powershell
./build.ps1 -SdkRoot "你的 .NET SDK 目录" -DalamudRoot "你的卫月程序集目录"
```

编译结果位于 `dist/`。
