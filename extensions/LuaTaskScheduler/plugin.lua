-- ============================================================================
-- 定时任务调度器（Lua 示例插件）
--
-- 排程模式：interval（每 N 秒）/ daily（每天 HH:mm）/ weekly（每周几）/ monthly（每月几号）/ once（指定日期）
-- 动作：rest（默认，调 UTSL REST）/ cmd（部署主机上执行 PowerShell）/ start（启动服务端）/ stop（停止服务端）
-- 选项：仅服务器在线时执行（配合 10 分钟宽限补跑，不会因为恰好离线而永久错过）
--
-- 这个插件同时是 Lua API 的"真实验收用例"：声明式面板 / 计时器 / 配置 / REST / 命令 / 服务端起停 全都用到。
-- ============================================================================

ctx:meta{ name = "定时任务", icon = "⏰", description = "定时间隔/每天/每周/每月/指定日期执行 REST、PowerShell 或启停服务端", author = "Zykor-Club" }

local GRACE_MIN = 10          -- 定点任务的宽限补跑窗口（分钟）
local TICK_SEC = 10           -- 调度检查粒度（秒）
local MODES = { "interval", "daily", "weekly", "monthly", "once" }
local ACTIONS = { "rest", "cmd", "start", "stop" }
local WD_NAMES = { "周一", "周二", "周三", "周四", "周五", "周六", "周日" }

local cfg = ctx:get_config()
local firstLoad = false
if type(cfg.tasks) ~= "table" then
  firstLoad = true
  cfg.tasks = {
    { name = "示例：每天 04:00 调一次 REST", enabled = false, mode = "daily", at = "04:00", action = "rest", url = "/world/list" },
  }
end
if type(cfg.last) ~= "table" then cfg.last = {} end
if type(cfg.log) ~= "table" then cfg.log = {} end
local function save() ctx:save_config(cfg) end
if firstLoad then save() end

local function parse_at(s)
  local h, m = string.match(tostring(s or ""), "^(%d%d?):(%d%d)$")
  return tonumber(h), tonumber(m)
end

local function mode_ok(t, now)
  local mode = t.mode or "interval"
  if mode == "daily" then return true end
  if mode == "weekly" then return now.wday == (tonumber(t.wday) or 1) end
  if mode == "monthly" then return now.day == (tonumber(t.mday) or 1) end
  if mode == "once" then return (t.date or "") == string.sub(now.str, 1, 10) end
  return false
end

-- 到点判定：纯函数（不碰 ctx），文件末尾导出以便自动化测试。
--  · interval：距上次执行满 every 秒
--  · 定点：计划时刻已到、且仍在宽限窗口内、且今天还没跑过
local function is_due(t, now, last_epoch, last_key)
  if not t.enabled then return false end
  local mode = t.mode or "interval"
  if mode == "interval" then
    local every = tonumber(t.every) or 60
    if every < 5 then every = 5 end
    return (now.epoch - (last_epoch or 0)) >= every
  end
  local hh, mm = parse_at(t.at)
  if hh == nil then return false end
  if not mode_ok(t, now) then return false end
  local nowMin = now.hour * 60 + now.min
  local planMin = hh * 60 + mm
  if nowMin < planMin or nowMin > planMin + GRACE_MIN then return false end
  return (last_key or "") ~= string.sub(now.str, 1, 10)
end

local function describe(t)
  local mode = t.mode or "interval"
  if mode == "interval" then return "每 " .. tostring(t.every or 60) .. " 秒" end
  local base = (t.at or "??:??")
  if mode == "daily" then return "每天 " .. base end
  if mode == "weekly" then return "每" .. (WD_NAMES[tonumber(t.wday) or 1] or "?") .. " " .. base end
  if mode == "monthly" then return "每月 " .. tostring(t.mday or 1) .. " 号 " .. base end
  if mode == "once" then return "仅 " .. tostring(t.date or "?") .. " " .. base end
  return base
end

local function action_text(t)
  local a = t.action or "rest"
  if a == "rest" then return "REST " .. tostring(t.url or "/world/list") end
  if a == "cmd" then return "命令 " .. string.sub(tostring(t.cmd or ""), 1, 40) end
  if a == "start" then return "启动服务端" end
  if a == "stop" then return "停止服务端" end
  return a
end

local function run_task(t, reason)
  local label = t.name or "(未命名)"
  local a = t.action or "rest"
  local ok, err
  if a == "start" then
    ok, err = pcall(function() return ctx:server_start(label) end)
    ctx:log("[定时任务] " .. label .. " → 启动服务端 " .. (ok and "已下发" or ("失败: " .. tostring(err))) .. "（" .. reason .. "）")
  elseif a == "stop" then
    ok, err = pcall(function() return ctx:server_stop(label) end)
    ctx:log("[定时任务] " .. label .. " → 停止服务端 " .. (ok and "已下发" or ("失败: " .. tostring(err))) .. "（" .. reason .. "）")
  elseif a == "cmd" then
    ok, err = pcall(function() return ctx:run_command(t.cmd or "Write-Output 空命令", 120000) end)
    ctx:log("[定时任务] " .. label .. " → " .. (ok and "命令执行完成" or ("命令失败: " .. tostring(err))) .. "（" .. reason .. "）：" .. string.sub(tostring(err), 1, 300))
  else
    ok, err = pcall(function() return ctx:rest_post(t.url or "/world/list") end)
    ctx:log("[定时任务] " .. label .. " → REST " .. tostring(t.url) .. (ok and (" 返回 " .. #tostring(err) .. " 字节") or (" 失败: " .. tostring(err))) .. "（" .. reason .. "）")
  end
  cfg.log[#cfg.log + 1] = ctx:now().str .. "  " .. label .. "  " .. action_text(t) .. "  " .. reason
  while #cfg.log > 30 do table.remove(cfg.log, 1) end
end

local function check(force)
  local now = ctx:now()
  local online = ctx:server_online()
  local changed = false
  for i, t in ipairs(cfg.tasks) do
    local rec = cfg.last[i] or {}
    if force or is_due(t, now, rec.epoch or 0, rec.key or "") then
      if t.online_only and not online and not force then
        cfg.log[#cfg.log + 1] = now.str .. "  跳过 " .. tostring(t.name) .. "：服务器不在线（仅在线时执行，等上线后在 " .. GRACE_MIN .. " 分钟内补跑）"
        while #cfg.log > 30 do table.remove(cfg.log, 1) end
        changed = true
      else
        run_task(t, force and "手动触发" or "定时触发")
        cfg.last[i] = { epoch = now.epoch, key = string.sub(now.str, 1, 10) }
        changed = true
      end
    end
  end
  if changed then save() end
end

ctx:every(TICK_SEC, function() check(false) end)

ctx:register_command("task", "定时任务：!task run = 立即全部执行；!task list = 列出", function(raw)
  local a = tostring(raw or ""):lower()
  if a:find("run") then check(true)
  elseif a:find("stop") then
    for i, t in ipairs(cfg.tasks) do t.enabled = false end; save(); ctx:log("[定时任务] 已停用全部任务")
  else
    for i, t in ipairs(cfg.tasks) do ctx:log("[定时任务] " .. i .. ". " .. tostring(t.name) .. " enabled=" .. tostring(t.enabled) .. " " .. describe(t)) end
  end
end)

ctx:register_status("定时任务", function()
  local n = 0
  for _, t in ipairs(cfg.tasks) do if t.enabled then n = n + 1 end end
  return n .. "/" .. #cfg.tasks .. (ctx:server_online() and " · 在线" or " · 离线")
end)
ctx:register_metric("已启用任务", "个", function()
  local n = 0
  for _, t in ipairs(cfg.tasks) do if t.enabled then n = n + 1 end end
  return n
end)

ctx:register_panel("定时任务", function()
  local i = tonumber(cfg.edit_index) or 1
  if i < 1 then i = 1 end
  if #cfg.tasks > 0 and i > #cfg.tasks then i = #cfg.tasks end
  local t = cfg.tasks[i] or {}
  local rows = {
    { kind = "hint", label = "服务器当前：" .. (ctx:server_online() and "在线" or "离线") .. "　|　共 " .. #cfg.tasks .. " 个任务，已启用 " .. (function() local n=0 for _,x in ipairs(cfg.tasks) do if x.enabled then n=n+1 end end return n end)() .. " 个" },
    { kind = "section", label = "任务列表（点一行即可切换编辑对象）" },
  }
  if #cfg.tasks == 0 then
    rows[#rows + 1] = { kind = "hint", label = "还没有任务，点「新增任务」开始。" }
  end
  for k, v in ipairs(cfg.tasks) do
    rows[#rows + 1] = {
      kind = "list",
      items = { {
        text = tostring(k) .. ". " .. tostring(v.name or "未命名"),
        sub = (v.enabled and "● 启用" or "○ 停用") .. "　" .. describe(v) .. "　→　" .. action_text(v) .. (v.online_only and "　(仅在线)" or ""),
        active = (k == i),
        key = tostring(k),
      } },
    }
  end
  rows[#rows + 1] = { kind = "section", label = "编辑第 " .. i .. " 个任务" }
  rows[#rows + 1] = { kind = "text", id = "name", label = "任务名", value = tostring(t.name or "") }
  rows[#rows + 1] = { kind = "checkbox", id = "enabled", label = "启用", value = t.enabled and true or false }
  rows[#rows + 1] = { kind = "checkbox", id = "online_only", label = "仅服务器在线时执行", value = t.online_only and true or false }
  rows[#rows + 1] = { kind = "select", id = "mode", label = "排程模式", options = MODES, value = tostring(t.mode or "interval") }
  rows[#rows + 1] = { kind = "number", id = "every", label = "间隔（秒）", value = tonumber(t.every or 60) }
  rows[#rows + 1] = { kind = "time", id = "at", label = "执行时刻", value = tostring(t.at or "04:00") }
  rows[#rows + 1] = { kind = "select", id = "wday", label = "星期几（weekly）", options = WD_NAMES, value = WD_NAMES[tonumber(t.wday) or 1] }
  rows[#rows + 1] = { kind = "number", id = "mday", label = "每月几号（monthly）", value = tonumber(t.mday or 1) }
  rows[#rows + 1] = { kind = "date", id = "date", label = "指定日期（once）", value = tostring(t.date or "") }
  rows[#rows + 1] = { kind = "select", id = "action", label = "执行动作", options = ACTIONS, value = tostring(t.action or "rest") }
  rows[#rows + 1] = { kind = "text", id = "url", label = "REST 路径（rest）", value = tostring(t.url or "/world/list") }
  rows[#rows + 1] = { kind = "text", id = "cmd", label = "PowerShell（cmd）", value = tostring(t.cmd or "") }
  rows[#rows + 1] = { kind = "buttons", items = {
    { text = "保存", action = "save" }, { text = "立即执行", action = "run" },
    { text = "新增任务", action = "add" }, { text = "复制一份", action = "dup" }, { text = "删除", action = "del" },
  } }
  rows[#rows + 1] = { kind = "section", label = "最近执行记录" }
  local n = #cfg.log
  if n == 0 then rows[#rows + 1] = { kind = "hint", label = "（还没有记录）" } end
  for k = math.max(1, n - 7), n do rows[#rows + 1] = { kind = "hint", label = cfg.log[k] } end
  return rows
end)

local function apply(t, v)
  if v.name ~= nil then t.name = tostring(v.name) end
  t.enabled = (v.enabled == true)
  t.online_only = (v.online_only == true)
  t.mode = tostring(v.mode or t.mode or "interval")
  t.every = tonumber(v.every) or t.every or 60
  t.at = tostring(v.at or t.at or "04:00")
  local wd = tostring(v.wday or "")
  for idx, nm in ipairs(WD_NAMES) do if nm == wd then t.wday = idx end end
  t.mday = tonumber(v.mday) or t.mday or 1
  t.date = tostring(v.date or "")
  t.action = tostring(v.action or t.action or "rest")
  t.url = tostring(v.url or t.url or "/world/list")
  t.cmd = tostring(v.cmd or t.cmd or "")
end

ctx:on_action(function(action, v)
  local i = tonumber(cfg.edit_index) or 1
  if action == "select" then
    cfg.edit_index = tonumber(tostring(v.clicked)) or i   -- ★ 点任务列表里的一行就切换编辑对象
    save()
    return
  end
  if i < 1 then i = 1 end
  if #cfg.tasks > 0 and i > #cfg.tasks then i = #cfg.tasks end
  if action == "add" then
    local nt = { name = "新任务 " .. (#cfg.tasks + 1), enabled = false, online_only = false, mode = "interval", every = 60, action = "rest", url = "/world/list" }
    cfg.tasks[#cfg.tasks + 1] = nt
    cfg.edit_index = #cfg.tasks
    save()
    ctx:log("[定时任务] 已新增任务（默认停用、默认 REST 动作）")
    return
  end
  if action == "dup" then
    local src = cfg.tasks[i]
    if src then
      local c = {}
      for k, x in pairs(src) do c[k] = x end
      c.name = tostring(src.name or "任务") .. " 副本"
      cfg.tasks[#cfg.tasks + 1] = c
      cfg.edit_index = #cfg.tasks
      save()
    end
    return
  end
  if action == "del" then
    if #cfg.tasks > 0 then
      table.remove(cfg.tasks, i)
      table.remove(cfg.last, i)
      cfg.edit_index = math.max(1, i - 1)
      save()
      ctx:log("[定时任务] 已删除第 " .. i .. " 个任务")
    end
    return
  end
  if action == "run" then
    local t = cfg.tasks[i]
    if t then
      apply(t, v)
      run_task(t, "手动触发")
      local now = ctx:now()
      cfg.last[i] = { epoch = now.epoch, key = string.sub(now.str, 1, 10) }
      save()
    end
    return
  end
  if action == "save" then
    local t = cfg.tasks[i]
    if t then
      apply(t, v)
      cfg.edit_index = i
      save()
      ctx:log("[定时任务] 已保存：" .. tostring(t.name) .. " " .. describe(t) .. " → " .. action_text(t))
    end
    return
  end
end)

-- ★ 导出纯函数给自动化测试用（插件运行本身不依赖它）
_G.utsl_scheduler_is_due = is_due
_G.utsl_scheduler_parse_at = parse_at
_G.utsl_scheduler_describe = describe
_G.utsl_scheduler_run_task = run_task
_G.utsl_scheduler_cfg = function() return cfg end

ctx:log("[定时任务] 已加载：" .. #cfg.tasks .. " 个任务；服务器当前 " .. (ctx:server_online() and "在线" or "离线") .. "；!task run 可立即全部执行")
