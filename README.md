# ModTranslater(CN)

翻译 Penumbra Mod 的英文选项和说明。支持扫描汉化、多选强制汉化、内置词组字典，以及 OpenAI 兼容接口和多种翻译 API。凭证在本地加密保存。

在卫月的自定义插件仓库中添加：

```text
https://raw.githubusercontent.com/MomijiOXO/FF14_ModTranslater/main/repo.json
```

启用 Penumbra 后输入 `/modtrans`，配置 API 并扫描。含中文的 Mod 在普通扫描中跳过；强制汉化保留中文和中英混合条目。

“恢复未翻译版本”可恢复后重新扫描汉化，当前无中文的 Mod 会跳过。重新导入未翻译的新版 Mod 后，扫描会重新建立对应的原文备份。

编译需要 .NET 10 SDK 和卫月 API 15 程序集，运行 `build.ps1` 并指定 `-SdkRoot`、`-DalamudRoot`。

扫描按当前有效 JSON 的选项与说明判断：无中文就汉化，含中文则跳过整个 Mod。处理记录不再跳过纯英文 Mod。
