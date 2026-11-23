#!/bin/bash
# ============================================================
# Ollama 全自动配置与调优脚本（macOS 专用）
# 前提：Ollama 已安装（本脚本会自动检测；缺失时会给出补救指引）
# 用法：打开「终端」，执行
#   bash ~/Downloads/Ollama全自动配置调优.sh
# 特点：自动识别芯片/内存/磁盘 → 自动选模型 → 自动写调优参数
#       → 自动重启服务 → 自动下载模型 → 自动试跑测速
# 可重复运行：已完成的步骤自动跳过，模型下载支持断点续传
# ============================================================
set -u

say()  { printf '\n\033[1;32m==> %s\033[0m\n' "$*"; }
warn() { printf '\033[1;33m[提示] %s\033[0m\n' "$*"; }
die()  { printf '\033[1;31m[错误] %s\033[0m\n' "$*"; exit 1; }

# ---------- 0. 自动检测本机配置 ----------
say "第 0 步：检测本机配置"
[ "$(uname -s)" = "Darwin" ] || die "本脚本只能在 macOS 上运行"

ARCH=$(uname -m)
RAM_BYTES=$(sysctl -n hw.memsize)
RAM_GB=$((RAM_BYTES / 1024 / 1024 / 1024))
CHIP=$(sysctl -n machdep.cpu.brand_string 2>/dev/null || echo "未知芯片")
AVAIL_KB=$(df -k "$HOME" | awk 'NR==2{print $4}')
AVAIL_GB=$((AVAIL_KB / 1024 / 1024))
MACOS_VER=$(sw_vers -productVersion)

echo "系统：macOS $MACOS_VER / $ARCH"
echo "芯片：$CHIP"
echo "内存：${RAM_GB}GB / 磁盘剩余：${AVAIL_GB}GB"

if [ "$ARCH" != "arm64" ]; then
  warn "检测到 Intel 芯片（或终端走了 Rosetta）。Apple Silicon 请确保用原生终端运行；Intel 机型只能 CPU 推理，速度较慢，建议选更小模型"
fi

# ---------- 1. 按内存自动选择模型与参数 ----------
say "第 1 步：按本机配置自动选择模型与调优档位"
if [ "$RAM_GB" -le 8 ]; then
  MODEL="qwen3:4b"        # 约 2.5GB，8GB 内存甜点区
  CTX=4096
  TIER="8GB 内存档：上下文 4096，单并发单模型，KV 缓存量化省内存"
elif [ "$RAM_GB" -le 16 ]; then
  MODEL="qwen3:8b"        # 约 5.2GB，16GB 内存主力
  CTX=8192
  TIER="16GB 内存档：上下文 8192，单并发单模型"
else
  MODEL="qwen3:14b"       # 约 9GB，32GB+ 内存主力
  CTX=16384
  TIER="32GB+ 内存档：上下文 16384，单并发单模型"
fi
if [ "$ARCH" != "arm64" ]; then
  MODEL="qwen3:1.7b"      # Intel 机型降档保速度
  CTX=4096
  TIER="Intel 档：小模型保速度，上下文 4096"
fi
echo "选定模型：$MODEL"
echo "调优档位：$TIER"

NEED_GB=6
[ "$RAM_GB" -ge 32 ] && NEED_GB=12
if [ "$AVAIL_GB" -lt "$NEED_GB" ]; then
  warn "磁盘剩余不足 ${NEED_GB}GB（模型+运行余量），可继续但建议先清理空间"
fi

# ---------- 2. 确认 Ollama 就绪 ----------
say "第 2 步：确认 Ollama 命令与服务"
if ! command -v ollama >/dev/null 2>&1; then
  if [ -x "/Applications/Ollama.app/Contents/Resources/ollama" ]; then
    warn "命令行工具未链接，尝试自动创建（可能要求输入开机密码）"
    sudo mkdir -p /usr/local/bin
    sudo ln -sf /Applications/Ollama.app/Contents/Resources/ollama /usr/local/bin/ollama
    export PATH="/usr/local/bin:$PATH"
  else
    die "未找到 Ollama。请先打开一次「应用程序」里的 Ollama 完成初始化；若尚未安装，到 https://ollama.com/download/mac 下载安装后重跑本脚本"
  fi
fi
command -v ollama >/dev/null 2>&1 || die "ollama 命令仍不可用：请重开终端后重跑本脚本"

if ! curl -s --max-time 3 http://localhost:11434/api/version >/dev/null 2>&1; then
  say "后台服务未运行，正在启动 Ollama（首次启动请在弹窗中点允许/同意）"
  open -a Ollama
  printf '等待服务启动'
  for _ in $(seq 1 30); do
    curl -s --max-time 2 http://localhost:11434/api/version >/dev/null 2>&1 && break
    printf '.'
    sleep 2
  done
  echo
fi

# ---------- 2.5 安装情况体检报告 ----------
say "安装情况体检报告（可截图发给助手帮你分析）"
echo "命令行位置：$(command -v ollama)"
if [ -d "/Applications/Ollama.app" ]; then
  echo "应用程序：/Applications/Ollama.app 已安装"
else
  warn "未在「应用程序」中找到 Ollama.app（可能是 brew 或其他方式安装）"
fi
echo "版本信息：$(ollama --version 2>/dev/null | tr '\n' ' ')"
if curl -s --max-time 3 http://localhost:11434/api/version >/dev/null 2>&1; then
  echo "后台服务：运行中（$(curl -s http://localhost:11434/api/version)）"
else
  warn "后台服务未运行，后续步骤会尝试启动"
fi
echo "已下载模型："
ollama list 2>/dev/null || echo "（暂无）"
echo "当前驻留模型："
ollama ps 2>/dev/null
echo "当前生效的调优参数："
for v in OLLAMA_CONTEXT_LENGTH OLLAMA_NUM_PARALLEL OLLAMA_MAX_LOADED_MODELS OLLAMA_KEEP_ALIVE OLLAMA_FLASH_ATTENTION OLLAMA_KV_CACHE_TYPE; do
  val=$(launchctl getenv "$v" 2>/dev/null)
  echo "  $v = ${val:-（未设置，使用默认值）}"
done
echo "内存压力概况："
memory_pressure 2>/dev/null | tail -1 || vm_stat | head -4

# ---------- 3. 写入调优参数并重启服务 ----------
say "第 3 步：写入调优参数（重启电脑后失效，重跑本脚本即可恢复）"
launchctl setenv OLLAMA_CONTEXT_LENGTH "$CTX"      # 上下文长度：越大越耗内存
launchctl setenv OLLAMA_NUM_PARALLEL 1             # 单并发：避免内存被多请求挤爆
launchctl setenv OLLAMA_MAX_LOADED_MODELS 1        # 只驻留一个模型
launchctl setenv OLLAMA_KEEP_ALIVE 10m             # 闲置 10 分钟后自动释放内存
launchctl setenv OLLAMA_FLASH_ATTENTION 1          # 开启 Flash Attention，提速省内存
launchctl setenv OLLAMA_KV_CACHE_TYPE q8_0         # KV 缓存量化，约省一半缓存内存
echo "已写入：上下文 $CTX / 单并发 / 单模型驻留 / 10分钟自动释放 / FlashAttention / KV缓存q8_0"
warn "若你的 Ollama 版本较旧、启动报错，可删除上面 KV_CACHE_TYPE 一行后重跑"

say "重启 Ollama 使参数生效"
osascript -e 'quit app "Ollama"' >/dev/null 2>&1
sleep 3
open -a Ollama
printf '等待后台服务启动'
for _ in $(seq 1 30); do
  curl -s --max-time 2 http://localhost:11434/api/version >/dev/null 2>&1 && break
  printf '.'
  sleep 2
done
echo
curl -s --max-time 3 http://localhost:11434/api/version >/dev/null 2>&1 \
  || die "后台服务未启动：请确认菜单栏出现羊驼图标后重跑本脚本"
echo "服务正常：$(curl -s http://localhost:11434/api/version)"

# ---------- 4. 下载主力模型 ----------
say "第 4 步：下载主力模型 $MODEL（中断后重跑本脚本可断点续传）"
if ollama list 2>/dev/null | awk '{print $1}' | grep -q "^${MODEL}$"; then
  echo "模型已存在，跳过下载"
else
  ollama pull "$MODEL" || die "模型下载失败：重跑可续传；网络长期不畅请给终端挂代理后重跑"
fi

# ---------- 5. 试跑 + 测速 + 验证 ----------
say "第 5 步：试跑一句话并输出速度统计（关注 eval rate，单位 token/s）"
ollama run "$MODEL" "你好，请用一句话介绍你自己" --verbose

say "确认运行状态 —— Apple Silicon 上 PROCESSOR 列应显示 100% GPU"
ollama ps

say "全部完成！"
cat <<EOF

本机配置：$CHIP / ${RAM_GB}GB 内存 / macOS $MACOS_VER
已配置模型：$MODEL（上下文 $CTX）

日常使用速查：
  ollama run $MODEL             进入对话（/bye 退出，/clear 清空上下文）
  ollama run $MODEL --verbose   带速度统计
  ollama stop $MODEL            立即释放内存
  ollama list                   查看已下载模型

使用建议：
  · 跑模型时关闭浏览器大标签页、虚拟机等占内存应用
  · 若 ollama ps 出现 CPU 占比，说明内存被挤占：关掉大户后 stop 再重跑
  · 调优参数重启电脑后会失效，重跑一次本脚本即可恢复
  · 想换模型：ollama pull 模型名（如 qwen3:8b），再 ollama run 即可
EOF
