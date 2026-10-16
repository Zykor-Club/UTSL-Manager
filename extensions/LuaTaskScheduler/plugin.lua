-- ============================================================================
-- 定时任务调度器（Lua 示例插件）
--
-- 功能：任务可按【间隔 N 分钟】/【每天 HH:mm】/【每周几 HH:mm】/【每月几号 HH:mm】执行；
--       动作支持 PowerShell 命令（ctx:run_command）或 UTSL REST（ctx:rest_post）。
--
-- 这个插件同时是 Lua API 的"真实验收用例"：如果 Lua 写不出一个真正能用的调度器，
-- 那就说明 API 设计有问题（所以它刻意用到了 声明式面板 / 计时器 / 配置 / 命令 / REST）。
-- ============================================================================

ctx:meta{ name = "定时任务", icon = "⏰", description = "间隔 / 每天 / 每周 / 每月定时执行命令或 REST", author = "Zykor-Club" }

local cfg = ctx:get_config()
local firstLoad = false
if type(cfg.tasks) ~= "table" then
  firstLoad = true
  cfg.tasks = {
    { name = "示例：每天 04:00 执行", enabled = false, mode = "daily", at = "04:00", action = "cmd", cmd = "Write-Output 这里写你的命令" },
  }
end
if type(cfg.last) ~= "table" then cfg.last = {} end
local function save() ctx:save_config(cfg) end
if firstLoad then save() end   -- ★ 首次加载就落盘：便于用文件编辑器直接改配置，也让"插件真的加载了"有磁盘证据

local function parse_at(s)
  local h, m = string.match(tostring(s or ""), "^(%d%d?):(%d%d)$")
  return tonumber(h), tonumber(m)
end

-- 到点判定：纯函数（不碰 ctx），文件末尾导出以便自动化测试。
-- now = ctx:now()；last_epoch/last_key = 该任务上次执行的记录
local function is_due(t, now, last_epoch, last_key)
  if not t.enabled then return false end
  local mode = t.mode or "interval"
  if mode == "interval" then
    local every = tonumber(t.every) or 30
    if every < 1 then every = 1 end
    return (now.epoch - (last_epoch or 0)) >= every * 60
  end
  local hh, mm = parse_at(t.at)
  if hh == nil then return false end
  if now.hour ~= hh or now.min ~= mm then return false end
  if mode == "weekly" and now.wday ~= (tonumber(t.wday) or 1) then return false end
  if mode == "monthly" and now.day ~= (tonumber(t.mday) or 1) then return false end
  return (last_key or "") ~= string.sub(now.str, 1, 10)   -- 同一天同一时刻只跑一次
end

local function run_task(t, reason)
  local label = t.name or "(未命名)"
  if t.action == "rest" then
    local ok, r = pcall(function() return ctx:rest_post(t.url or "/world/list") end)
    ctx:log("[定时任务] " .. label .. " → REST " .. tostring(t.url) .. (ok and (" 返回 " .. #tostring(r) .. " 字节") or (" 失败: " .. tostring(r))) .. "（" .. reason .. "）")
  else
    local ok, r = pcall(function() return ctx:run_command(t.cmd or "Write-Output 空命令", 120000) end)
    ctx:log("[定时任务] " .. label .. " → " .. (ok and "命令执行完成" or "命令失败") .. "（" .. reason .. "）：" .. string.sub(tostring(r), 1, 300))
  end
end

local function check(force)
  local now = ctx:now()
  for i, t in ipairs(cfg.tasks) do
    local rec = cfg.last[i] or {}
    if force or is_due(t, now, rec.epoch or 0, rec.key or "") then
      run_task(t, force and "手动触发" or "定时触发")
      cfg.last[i] = { epoch = now.epoch, key = string.sub(now.str, 1, 10) }
      save()
    end
  end
end

ctx:every(20, function() check(false) end)   -- 20 秒粒度；定点任务在那一分钟内只命中一次
ctx:register_command("task", "定时任务：!task run|list —— 立即执行 / 列出", function(raw)
  local a = tostring(raw or ""):lower()
  if a:find("run") and a:find("all") then check(true)
  elseif a:find("run") then check(true)
  else
    for i, t in ipairs(cfg.tasks) do ctx:log("[定时任务] " .. i .. ". " .. tostring(t.name) .. " enabled=" .. tostring(t.enabled) .. " mode=" .. tostring(t.mode)) end
  end
end)

local function row(i) return cfg.tasks[i] or {} end
local function S(i, k, d) local v = row(i)[k]; if v == nil then return d end; return v end

ctx:register_status("定时任务", function() return "共 " .. #cfg.tasks .. " 个" end)
ctx:register_metric("已启用任务", "个", function()
  local n = 0
  for _, t in ipairs(cfg.tasks) do if t.enabled then n = n + 1 end end
  return n
end)

ctx:register_panel("定时任务", function()
  local i = tonumber(cfg.edit_index) or 1
  if i < 1 then i = 1 end
  if i > #cfg.tasks then i = #cfg.tasks end
  if i < 1 then i = 1 end
  local t = row(i)
  local rows = {
    { kind = "text", label = "任务数", value = tostring(#cfg.tasks) .. "（正在编辑第 " .. i .. " 个）" },
  }
  for k, v in ipairs(cfg.tasks) do
    local when = (v.mode == "interval") and ("每 " .. tostring(v.every or 30) .. " 分钟") or ((v.mode or "daily") .. " " .. tostring(v.at or ""))
    rows[#rows + 1] = { kind = "text", label = "  " .. k .. ". " .. tostring(v.name or ""), value = (v.enabled and "启用" or "停用") .. " · " .. when }
  end
  rows[#rows + 1] = { kind = "divider" }
  rows[#rows + 1] = { kind = "number", id = "idx", label = "编辑第几个任务", value = i }
  rows[#rows + 1] = { kind = "text", id = "name", label = "任务名", value = tostring(S(i, "name", "新任务")) }
  rows[#rows + 1] = { kind = "select", id = "mode", label = "模式", options = { "interval", "daily", "weekly", "monthly" }, value = tostring(S(i, "mode", "interval")) }
  rows[#rows + 1] = { kind = "number", id = "every", label = "间隔（分钟，interval 用）", value = tonumber(S(i, "every", 30)) }
  rows[#rows + 1] = { kind = "text", id = "at", label = "时刻 HH:mm（daily/weekly/monthly 用）", value = tostring(S(i, "at", "04:00")) }
  rows[#rows + 1] = { kind = "number", id = "wday", label = "星期几 1-7（weekly 用）", value = tonumber(S(i, "wday", 1)) }
  rows[#rows + 1] = { kind = "number", id = "mday", label = "每月几号（monthly 用）", value = tonumber(S(i, "mday", 1)) }
  rows[#rows + 1] = { kind = "select", id = "action", label = "动作类型", options = { "cmd", "rest" }, value = tostring(S(i, "action", "cmd")) }
  rows[#rows + 1] = { kind = "text", id = "cmd", label = "PowerShell 命令（action=cmd）", value = tostring(S(i, "cmd", "")) }
  rows[#rows + 1] = { kind = "text", id = "url", label = "REST 路径（action=rest）", value = tostring(S(i, "url", "/world/list")) }
  rows[#rows + 1] = { kind = "select", id = "enabled", label = "是否启用", options = { "yes", "no" }, value = (row(i).enabled and "yes" or "no") }
  rows[#rows + 1] = { kind = "buttons", items = {
    { text = "保存", action = "save" }, { text = "立即执行", action = "run" },
    { text = "新增任务", action = "add" }, { text = "删除该任务", action = "del" }, { text = "上一个", action = "prev" }, { text = "下一个", action = "next" },
  } }
  return rows
end)

ctx:on_action(function(action, v)
  local i = tonumber(v.idx) or 1
  if i < 1 then i = 1 end
  if i > #cfg.tasks then i = #cfg.tasks end
  if action == "prev" then cfg.edit_index = math.max(1, i - 1); save(); return end
  if action == "next" then cfg.edit_index = math.min(#cfg.tasks, i + 1); save(); return end
  if action == "add" then
    cfg.tasks[#cfg.tasks + 1] = { name = "新任务 " .. (#cfg.tasks + 1), enabled = false, mode = "interval", every = 30, action = "cmd", cmd = "" }
    cfg.edit_index = #cfg.tasks; save(); ctx:log("[定时任务] 已新增第 " .. #cfg.tasks .. " 个任务（默认停用）")
    return
  end
  if action == "del" then
    if #cfg.tasks > 0 then table.remove(cfg.tasks, i); cfg.last[i] = nil; save(); ctx:log("[定时任务] 已删除第 " .. i .. " 个任务") end
    return
  end
  if action == "run" then
    local t = cfg.tasks[i]
    if t then run_task(t, "手动触发"); local now = ctx:now(); cfg.last[i] = { epoch = now.epoch, key = string.sub(now.str, 1, 10) }; save() end
    return
  end
  if action == "save" then
    local t = cfg.tasks[i]
    if not t then return end
    t.name = tostring(v.name or t.name)
    t.mode = tostring(v.mode or t.mode)
    t.every = tonumber(v.every) or t.every
    t.at = tostring(v.at or t.at)
    t.wday = tonumber(v.wday) or t.wday
    t.mday = tonumber(v.mday) or t.mday
    t.action = tostring(v.action or t.action)
    t.cmd = tostring(v.cmd or t.cmd or "")
    t.url = tostring(v.url or t.url or "")
    t.enabled = (tostring(v.enabled) == "yes")
    cfg.edit_index = i
    save()
    ctx:log("[定时任务] 已保存第 " .. i .. " 个任务：" .. tostring(t.name) .. " enabled=" .. tostring(t.enabled) .. " mode=" .. tostring(t.mode))
    return
  end
end)

-- ★ 导出纯函数给自动化测试用（插件运行本身不依赖它）
_G.utsl_scheduler_is_due = is_due
_G.utsl_scheduler_parse_at = parse_at

ctx:log("[定时任务] 已加载，共 " .. #cfg.tasks .. " 个任务；用「!task run」可立即全部执行一次")
