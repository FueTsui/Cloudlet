# Cloudlet 品牌图标

图标来自用户指定的 `output/imagegen/cloud-soft-3d-v2/cloud-black.png` 和 `cloud-white.png`。原图保持不变。

- `icon-black.png`、`icon-white.png` 是源 PNG 的逐字节副本，保留原始画布及透明度。
- `icon-black.ico`、`icon-white.ico` 各含 16、20、24、32、40、48、64、128、256 像素九种尺寸，使用 32 位透明 PNG 帧。
- `icon.ico` 与 `icon.png` 默认使用黑云，分别与 `icon-black.ico` 和 `icon-black.png` 逐字节相同。
- 仅将完整源画布按比例缩小编码为 Windows 图标，没有重新生成、调色、裁剪、去背景或修整边缘；源图中的细节与边缘特征均保留。

在仓库根目录执行 `pwsh -File .\scripts\build-icons.ps1` 可重复导出。`icon-manifest.json` 记录源文件及输出 SHA256、尺寸、透明/半透明/不透明像素统计、ICO 目录和每一帧的 Windows 解码检查。

这些验证确认图标文件结构、透明度和 Windows 图标解码可用；不代表已经检查安装器、任务栏或托盘中的实际显示效果。
