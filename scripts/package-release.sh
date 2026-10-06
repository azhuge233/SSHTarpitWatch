#!/usr/bin/env bash
# SSHTarpitWatch 发布打包：构建 linux-x64 原生 AOT 单文件并打成 tar.gz。
#
# 用法：scripts/package-release.sh [输出目录]    默认输出目录 = <仓库>/dist（.gitignore 已忽略）
# 环境：Linux x86-64 + .NET 10 SDK（NativeAOT 另需 clang 与 zlib 开发头文件）
#
# 流程：源码复制到 $HOME 下的临时工作目录（避开 /mnt/c 性能损耗）→ dotnet publish
#       → 从产物 --version 读取版本号（单一出处 = csproj <Version>）→ 组装包目录（二进制 + 配置样例共 2 件；不含 README，也不含 systemd 单元——单元由用户照 README 模板自建）→ 打 tar.gz。
set -euo pipefail

repo_dir=$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)
out_dir=${1:-"$repo_dir/dist"}

if ! command -v dotnet >/dev/null 2>&1; then
	if [ -x "$HOME/.dotnet/dotnet" ]; then
		export PATH="$HOME/.dotnet:$PATH"
	else
		echo "error: dotnet not found (install the .NET SDK or add it to PATH)" >&2
		exit 1
	fi
fi

work_dir=$(mktemp -d "$HOME/.sshtarpitwatch-package.XXXXXX")
cleanup() {
	if [ -n "${work_dir:-}" ] && [ -d "$work_dir" ]; then
		rm -rf -- "$work_dir"
	fi
}
trap cleanup EXIT
echo "==> work dir: $work_dir"

echo "==> copy source (bin/obj excluded)"
mkdir -p "$work_dir/src"
tar -C "$repo_dir/src" --exclude=./bin --exclude=./obj -cf - . | tar -C "$work_dir/src" -xf -

echo "==> dotnet publish (Release / linux-x64 / NativeAOT)"
dotnet publish "$work_dir/src/SSHTarpitWatch.csproj" -c Release -r linux-x64 -o "$work_dir/publish" --nologo

binary=$(find "$work_dir/publish" -maxdepth 1 -type f \( -name 'sshtarpitwatch' -o -name 'SSHTarpitWatch' \) -print -quit)
if [ -z "$binary" ]; then
	echo "error: published binary not found in $work_dir/publish" >&2
	exit 1
fi

version_line=$("$binary" --version)
version=${version_line#SSHTarpitWatch }
case "$version" in
	[0-9]*) ;;
	*)
		echo "error: cannot parse version from --version output: \"$version_line\"" >&2
		exit 1
		;;
esac
echo "==> version: $version (--version said: $version_line)"

pkg_name="sshtarpitwatch-$version-linux-x64"
staging="$work_dir/staging/$pkg_name"
mkdir -p "$staging"
cp "$binary" "$staging/sshtarpitwatch"
cp "$repo_dir/config/config.example.json" "$staging/config.example.json"

# 统一权限位（源码经 /mnt/c 等挂载时 cp 出的权限位不可靠）：可执行 755，其余 644
chmod 755 "$staging/sshtarpitwatch"
chmod 644 "$staging/config.example.json"

mkdir -p "$out_dir"
tarball="$out_dir/$pkg_name.tar.gz"
echo "==> pack: $tarball"
tar -C "$work_dir/staging" -czf "$tarball" "$pkg_name"

ls -l "$tarball"
if command -v sha256sum >/dev/null 2>&1; then
	sha256sum "$tarball"
fi
echo "==> done"
