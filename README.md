# ModTranslater(CN)

翻译 Penumbra Mod 的英文选项和说明。支持增量扫描、多选强制汉化、内置词组字典，以及 OpenAI 兼容接口和多种翻译 API。凭证在本地加密保存。

在卫月的自定义插件仓库中添加：

```text
https://raw.githubusercontent.com/MomijiOXO/FF14_ModTranslater/main/repo.json
```

启用 Penumbra 后输入 `/modtrans`，配置 API 并扫描。含中文的 Mod 在普通扫描中跳过；强制汉化保留中文和中英混合条目。

“恢复所选原版”恢复最早保存的版本，后续汉化不会覆盖首次备份。旧记录从历史备份迁移；若最早原文已缺失，无法凭空找回英文。Mod 已更新或编辑时拒绝覆盖。

编译需要 .NET 10 SDK 和卫月 API 15 程序集，运行 `build.ps1` 并指定 `-SdkRoot`、`-DalamudRoot`。
