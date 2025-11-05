# 发布新版本指南

## 自动发布流程

本项目配置了自动 Release 流程。当您推送一个版本标签（tag）时，GitHub Actions 会自动：

1. 构建 Release 版本
2. 创建 ZIP 压缩包
3. 创建 GitHub Release
4. 上传编译好的应用程序

## 如何发布新版本

### 1. 确保所有更改已提交并推送

```bash
git add .
git commit -m "你的提交信息"
git push origin main
```

### 2. 创建版本标签

```bash
# 创建标签（使用语义化版本号）
git tag v1.0.0

# 或者创建带注释的标签
git tag -a v1.0.0 -m "Release version 1.0.0"
```

### 3. 推送标签到 GitHub

```bash
git push origin v1.0.0
```

### 4. 自动构建

推送标签后，GitHub Actions 会自动：
- 触发 Release workflow
- 构建应用程序
- 创建 GitHub Release
- 上传 ZIP 文件

您可以在 GitHub 仓库的 **Actions** 页面查看构建进度。

### 5. 查看 Release

完成后，在仓库的 **Releases** 页面可以看到新创建的版本，用户可以直接下载 ZIP 文件使用。

## 版本号规范

建议使用[语义化版本号](https://semver.org/lang/zh-CN/)：

- **v1.0.0** - 主版本.次版本.修订号
- **v1.2.0** - 新功能发布
- **v1.2.1** - Bug 修复

## 示例

```bash
# 修复了一些 bug，发布 1.0.1 版本
git tag v1.0.1 -m "修复同步判断逻辑问题"
git push origin v1.0.1

# 添加了新功能，发布 1.1.0 版本
git tag v1.1.0 -m "添加日志系统和右键菜单功能"
git push origin v1.1.0
```

## 删除错误的标签

如果创建了错误的标签，可以这样删除：

```bash
# 删除本地标签
git tag -d v1.0.0

# 删除远程标签
git push origin --delete v1.0.0
```

## 注意事项

- 只有推送到远程的标签才会触发 Release workflow
- 标签必须以 `v` 开头（如 v1.0.0）
- 建议在 main 分支上创建标签
- 每个标签只能创建一次 Release
