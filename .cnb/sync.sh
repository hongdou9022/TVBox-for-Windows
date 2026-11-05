#!/usr/bin/env bash
# sync.sh — GitHub <-> CNB 手动双向对齐（本地 CLI 与 CNB 按钮流水线共用同一份逻辑）
#
# 设计前提：同一时间只在一端开发，偶尔手动触发一次同步。
# 绝不自动跑、绝不默认强推：只在「一端是另一端的祖先」时做 fast-forward；
# 两端分叉时拒绝覆盖并报警，人工处理。
#
# 约定（2026-11-05）：只同步 sync_dev 一条分支。
#   - 实际同步时 --branch 只允许 sync_dev；
#   - --branch all 仅在 --dry-run true 下用于全量只读体检；
#   - tag 不再持续同步（首迁已一次性搬完），如需补齐用 --tags true 显式开启。
#
# 用法:
#   ./sync.sh --repo BoxWindows
#   ./sync.sh --repo-slug floatedcloud/cloudtv_win --dry-run true
#   ./sync.sh --repo backend --direction to-github
#   ./sync.sh --repo Box --branch all --dry-run true      # 全量只读体检
#
# 参数:
#   --repo <key>        repos.conf 第 1 列（本地目录名），与 --repo-slug 二选一
#   --repo-slug <slug>  CNB 完整路径（流水线用 CNB_REPO_SLUG 传入）
#   --branch <sync_dev|all> 默认 sync_dev；all 只允许配合 --dry-run true
#   --direction <auto|to-github|to-cnb>  默认 auto（谁落后就推谁）
#   --dry-run <true|false>  默认 false；true 只出报告、不写任何一端
#   --force <true|false>    默认 false；仅在分叉且 direction 明确时，
#                           用 --force-with-lease 按指定方向覆盖（危险）
#   --tags <true|false>     默认 false；开启后 tag 单向 GitHub -> CNB，同名不同 SHA 不覆盖
#
# 环境变量:
#   GITHUB_TOKEN  GitHub PAT（私有仓读写）
#   CNB_TOKEN     CNB 访问令牌（流水线内置临时令牌，或本地长期令牌）
#   CNB_PAT       可选；优先于 CNB_TOKEN（当内置令牌只有只读权限时用）
#   CNB_GROUP     CNB 组织，默认 floatedcloud
#   SYNC_CONF     映射表路径，默认脚本同目录 repos.conf
#   SYNC_WORKDIR  bare 缓存目录，默认 ~/.cache/cnb-sync
#
# 退出码: 0 成功 / 1 推送失败 / 2 存在未解决的分叉 / 3 参数或配置错误

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
CONF="${SYNC_CONF:-${SCRIPT_DIR}/repos.conf}"
GROUP="${CNB_GROUP:-floatedcloud}"
WORK="${SYNC_WORKDIR:-${HOME}/.cache/cnb-sync}"

REPO_KEY=""
REPO_SLUG=""
BRANCH="sync_dev"
DIRECTION="auto"
DRY="false"
FORCE="false"
SYNC_TAGS="false"

log()  { echo "[$(date '+%H:%M:%S')] $*"; }
info() { log "  $*"; }
ok()   { log "OK  $*"; }
warn() { log "!!  $*"; }
die()  { log "ERR $*"; exit 3; }

usage() { sed -n '2,32p' "$0" | sed 's/^# \{0,1\}//'; }

while [ $# -gt 0 ]; do
  case "$1" in
    --repo)       REPO_KEY="$2";  shift 2 ;;
    --repo-slug)  REPO_SLUG="$2"; shift 2 ;;
    --branch)     BRANCH="$2";    shift 2 ;;
    --direction)  DIRECTION="$2"; shift 2 ;;
    --dry-run)    DRY="$2";       shift 2 ;;
    --force)      FORCE="$2";     shift 2 ;;
    --tags)       SYNC_TAGS="$2"; shift 2 ;;
    -h|--help)    usage; exit 0 ;;
    *) die "未知参数: $1" ;;
  esac
done

[ -n "$REPO_KEY" ] || [ -n "$REPO_SLUG" ] || die "必须指定 --repo 或 --repo-slug"
case "$DIRECTION" in auto|to-github|to-cnb) ;; *) die "direction 只能是 auto / to-github / to-cnb" ;; esac
case "$DRY" in true|false) ;; *) die "dry-run 只能是 true / false" ;; esac
case "$FORCE" in true|false) ;; *) die "force 只能是 true / false" ;; esac
case "$SYNC_TAGS" in true|false) ;; *) die "tags 只能是 true / false" ;; esac
case "$BRANCH" in
  ""|*[!A-Za-z0-9._/-]*) die "非法分支名: ${BRANCH}" ;;
esac
# 硬护栏：按约定只同步 sync_dev；all 仅允许只读体检，杜绝误同步其它分支
if [ "$DRY" != "true" ] && [ "$BRANCH" != "sync_dev" ]; then
  die "按约定只同步 sync_dev，拒绝 --branch ${BRANCH}（要看全量差异请加 --dry-run true）"
fi

[ -f "$CONF" ] || die "找不到映射表: $CONF"

FOUND=""; KEY=""; GH_REPO=""; CNB_REPO=""; DEFAULT_BRANCH=""; DEV_BRANCH=""; HAS_LFS="no"
VALID_KEYS=""
while IFS= read -r line || [ -n "$line" ]; do
  line="${line%$'\r'}"
  case "$line" in ''|\#*) continue ;; esac
  IFS='|' read -r k g c d v l rest <<< "$line"
  [ -z "${k:-}" ] && continue
  VALID_KEYS="${VALID_KEYS} ${k}"
  if { [ -n "$REPO_KEY" ] && [ "$k" = "$REPO_KEY" ]; } || \
     { [ -n "$REPO_SLUG" ] && [ "${GROUP}/${c}" = "$REPO_SLUG" ]; }; then
    FOUND="yes"; KEY="$k"; GH_REPO="$g"; CNB_REPO="$c"
    DEFAULT_BRANCH="$d"; DEV_BRANCH="$v"; HAS_LFS="${l:-no}"
  fi
done < "$CONF"
[ -n "$FOUND" ] || die "映射表里找不到该仓库。可用 key:${VALID_KEYS}"

GH_TOKEN="${GITHUB_TOKEN:-}"
CNB_TOK="${CNB_PAT:-${CNB_TOKEN:-}}"
# SYNC_GH_URL / SYNC_CNB_URL 可用于本地裸仓库自测或自建 Git 服务；
# 未设置时才要求对应平台的令牌。
[ -n "${SYNC_GH_URL:-}" ]  || [ -n "$GH_TOKEN" ] || die "GITHUB_TOKEN 未设置"
[ -n "${SYNC_CNB_URL:-}" ] || [ -n "$CNB_TOK" ]  || die "CNB_TOKEN / CNB_PAT 未设置"

BARE="${WORK}/${KEY}.git"
mkdir -p "$WORK"
if [ ! -d "${BARE}/objects" ]; then
  # git init 对已存在目录是幂等的；不用 rm，避免 Windows 路径下的安全删除限制
  git init --bare -q "$BARE"
fi

# token 内嵌在 URL 里：脚本全程不打印 remote 配置，避免日志泄露凭据
GH_URL="${SYNC_GH_URL:-https://oauth2:${GH_TOKEN}@github.com/${GH_REPO}.git}"
CNB_URL="${SYNC_CNB_URL:-https://cnb:${CNB_TOK}@cnb.cool/${GROUP}/${CNB_REPO}.git}"
git -C "$BARE" config remote.gh.url  "$GH_URL"
git -C "$BARE" config remote.cnb.url "$CNB_URL"

log "仓库 ${KEY}: github=${GH_REPO} <-> cnb=${GROUP}/${CNB_REPO}"
log "范围 branch=${BRANCH} direction=${DIRECTION} dry-run=${DRY} force=${FORCE} tags=${SYNC_TAGS} lfs=${HAS_LFS}"

for r in gh cnb; do
  git -C "$BARE" fetch -q --no-tags --prune "$r" "+refs/heads/*:refs/remotes/${r}/*" \
    || die "fetch ${r} 失败（检查 token 权限 / 网络 / 仓库是否存在）"
done

branches=$(
  { git -C "$BARE" for-each-ref --format='%(refname:short)' refs/remotes/gh/
    git -C "$BARE" for-each-ref --format='%(refname:short)' refs/remotes/cnb/ ; } \
  | sed -E 's#^(gh|cnb)/##' | sort -u
)

if [ "$BRANCH" != "all" ]; then
  if ! printf '%s\n' "$branches" | grep -qx -- "$BRANCH"; then
    die "分支 ${BRANCH} 在两端都不存在。现有分支: $(echo "$branches" | tr '\n' ' ')"
  fi
  branches="$BRANCH"
fi

ERR_FILE="${BARE}/.sync-push-error"
total=0; same=0; aligned=0; skipped=0; failed=0; unresolved=0

push_ref() {
  # $1=目标 remote  $2=源 SHA  $3=分支  $4=lease 旧 SHA  $5=是否 lease（仅显式 force 时为 true）
  # 普通路径一律用不带 force 的 push，由 git 自己保证 fast-forward
  local target="$1" sha="$2" b="$3" old="$4" lease="$5"
  if [ "$DRY" = "true" ]; then
    info "[预览] 将把 ${b} 推到 ${target} (${sha:0:8})"
    return 0
  fi
  if [ "$lease" = "true" ] && [ -n "$old" ]; then
    git -C "$BARE" push -q --force-with-lease="refs/heads/${b}:${old}" \
      "$target" "${sha}:refs/heads/${b}" 2>"$ERR_FILE"
  else
    git -C "$BARE" push -q "$target" "${sha}:refs/heads/${b}" 2>"$ERR_FILE"
  fi
}

while IFS= read -r b; do
  [ -z "$b" ] && continue
  total=$((total + 1))
  gh_sha=$(git -C "$BARE" rev-parse --verify --quiet "refs/remotes/gh/${b}^{commit}"  || true)
  cnb_sha=$(git -C "$BARE" rev-parse --verify --quiet "refs/remotes/cnb/${b}^{commit}" || true)

  target=""; src_sha=""; old_sha=""; state=""; lease="false"

  if [ -n "$gh_sha" ] && [ "$gh_sha" = "$cnb_sha" ]; then
    state="same"
  elif [ -z "$gh_sha" ]; then
    state="missing-github"; target="gh";  src_sha="$cnb_sha"; old_sha=""
  elif [ -z "$cnb_sha" ]; then
    state="missing-cnb";    target="cnb"; src_sha="$gh_sha";  old_sha=""
  elif git -C "$BARE" merge-base --is-ancestor "$gh_sha" "$cnb_sha"; then
    state="cnb-ahead";      target="gh";  src_sha="$cnb_sha"; old_sha="$gh_sha"
  elif git -C "$BARE" merge-base --is-ancestor "$cnb_sha" "$gh_sha"; then
    state="github-ahead";   target="cnb"; src_sha="$gh_sha";  old_sha="$cnb_sha"
  else
    state="diverged"
  fi

  if [ "$state" = "same" ]; then
    info "${b}: 已一致 ${gh_sha:0:8}"
    same=$((same + 1)); continue
  fi

  if [ "$state" = "diverged" ]; then
    if [ "$FORCE" = "true" ] && [ "$DIRECTION" != "auto" ]; then
      warn "${b}: 分叉，按 --direction ${DIRECTION} --force 覆盖（--force-with-lease 保护）"
      lease="true"
      if [ "$DIRECTION" = "to-github" ]; then
        target="gh"; src_sha="$cnb_sha"; old_sha="$gh_sha"
      else
        target="cnb"; src_sha="$gh_sha"; old_sha="$cnb_sha"
      fi
    else
      warn "${b}: 分叉！gh=${gh_sha:0:8} cnb=${cnb_sha:0:8}，拒绝覆盖；人工 merge 后重跑"
      unresolved=$((unresolved + 1)); continue
    fi
  fi

  if [ "$DIRECTION" = "to-github" ] && [ "$target" != "gh" ]; then
    info "${b}: 需写 CNB，但方向限定 to-github，跳过"; skipped=$((skipped + 1)); continue
  fi
  if [ "$DIRECTION" = "to-cnb" ] && [ "$target" != "cnb" ]; then
    info "${b}: 需写 GitHub，但方向限定 to-cnb，跳过"; skipped=$((skipped + 1)); continue
  fi

  if push_ref "$target" "$src_sha" "$b" "$old_sha" "$lease"; then
    if [ "$DRY" = "true" ]; then aligned=$((aligned + 1));
    else ok "${b}: ${state} -> 已对齐 ${target} (${src_sha:0:8})"; aligned=$((aligned + 1)); fi
  else
    warn "${b}: 推送到 ${target} 失败: $(tr '\n' ' ' < "$ERR_FILE" | cut -c1-220)"
    failed=$((failed + 1))
  fi
done <<< "$branches"

# tag：默认关闭；开启时只单向 GitHub -> CNB，同名不同 SHA 一律不覆盖
if [ "$SYNC_TAGS" = "true" ] && [ "$DIRECTION" != "to-github" ]; then
  gh_tags=$(git -C "$BARE" ls-remote --tags gh  2>/dev/null | grep -v '\^{}$' \
            | awk '{sub(/^refs\/tags\//,"",$2); print $2" "$1}' || true)
  cnb_tags=$(git -C "$BARE" ls-remote --tags cnb 2>/dev/null | grep -v '\^{}$' \
             | awk '{sub(/^refs\/tags\//,"",$2); print $2" "$1}' || true)
  while IFS= read -r row; do
    [ -z "$row" ] && continue
    t="${row%% *}"; sha="${row##* }"
    other=$(printf '%s\n' "$cnb_tags" | awk -v t="$t" '$1==t{print $2}')
    [ "$other" = "$sha" ] && continue
    if [ -n "$other" ]; then
      warn "tag ${t}: 两端 SHA 不同，不覆盖（tag 不自动改写）"; continue
    fi
    if [ "$DRY" = "true" ]; then
      info "[预览] 将把 tag ${t} 推到 CNB"; aligned=$((aligned + 1)); continue
    fi
    if git -C "$BARE" fetch -q --no-tags gh "+refs/tags/${t}:refs/tags/${t}" 2>/dev/null \
       && git -C "$BARE" push -q cnb "refs/tags/${t}:refs/tags/${t}" 2>"$ERR_FILE"; then
      ok "tag ${t}: 已推到 CNB"; aligned=$((aligned + 1))
    else
      warn "tag ${t}: 推送失败: $(tr '\n' ' ' < "$ERR_FILE" 2>/dev/null | cut -c1-180)"
      failed=$((failed + 1))
    fi
  done <<< "$gh_tags"
fi

# LFS 对象补齐（幂等；bare 缓存里已有的 LFS 对象双向补一遍）
if [ "$HAS_LFS" = "yes" ] && [ "$DRY" != "true" ] && [ "$aligned" -gt 0 ]; then
  if command -v git-lfs >/dev/null 2>&1; then
    for r in gh cnb; do
      git -C "$BARE" lfs fetch --all "$r" >/dev/null 2>&1 || true
      git -C "$BARE" lfs push  --all "$r" >/dev/null 2>&1 \
        || warn "LFS 对象补齐到 ${r} 失败（如有大文件改动，请用本地 CLI 重跑）"
    done
    ok "LFS 对象已双向补齐"
  else
    warn "未安装 git-lfs，跳过 LFS 对象同步（BoxWindows 请在本地跑一次 sync.sh）"
  fi
fi

log "汇总 ${KEY}: 分支 ${total} | 已一致 ${same} | 本次对齐 ${aligned} | 跳过 ${skipped} | 未解决分叉 ${unresolved} | 失败 ${failed}"
[ "$failed" -eq 0 ] || exit 1
if [ "$unresolved" -gt 0 ]; then
  warn "存在分叉：默认不覆盖。人工 merge 后重跑；确需覆盖则用 --direction to-github|to-cnb --force true"
  exit 2
fi
exit 0
