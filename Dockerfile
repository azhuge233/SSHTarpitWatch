# SSHTarpitWatch 容器镜像
#
# 构建上下文要求（context 根目录下必须同时有本文件与已编译的 sshtarpitwatch 二进制）：
#   bash scripts/package-release.sh
#   mkdir -p /tmp/sshtw-ctx && tar -xzf dist/sshtarpitwatch-*-linux-x64.tar.gz -C /tmp/sshtw-ctx
#   cp Dockerfile .dockerignore /tmp/sshtw-ctx/
#   docker build /tmp/sshtw-ctx -t sshtarpitwatch:local
#
# 基础镜像 = .NET 官方 runtime-deps 的 chiseled 变体（Ubuntu noble 底座；无 shell、无包管理器、默认非 root）。
# 它自带本程序运行所需的 libc / libm / libssl3 / CA 证书；时区数据不在其中——
# 运行容器时挂载宿主的 /etc/localtime 即可（见 README 的 Docker 一节）。
FROM mcr.microsoft.com/dotnet/runtime-deps:10.0-noble-chiseled

LABEL org.opencontainers.image.title="SSHTarpitWatch" \
      org.opencontainers.image.description="SSH tarpit with connection notifications" \
      org.opencontainers.image.source="https://github.com/azhuge233/SSHTarpitWatch"

WORKDIR /app
COPY sshtarpitwatch /app/sshtarpitwatch

# 配置默认读 /data/config.json：挂载"目录"而不是单个文件，
# 这样编辑器替换文件后 SIGHUP 热重载才能可靠读到新内容
ENTRYPOINT ["/app/sshtarpitwatch"]
CMD ["--config", "/data/config.json"]
