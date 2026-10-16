-- Lua 冒烟测试插件：验证 .lua 发现、ctx 桥、沙箱、配置读写、命令、声明式面板整条链路。
ctx:meta{ name = "Lua 冒烟测试", icon = "🧪", description = "验证 Lua 插件通道（可随时删除）", author = "Zykor-Club" }

-- ① 沙箱自检：os / io / require 应当不可用（这是安全边界，必须在真插件里也确认一次）
ctx:log("[LuaSmoke] 沙箱检查 os=" .. tostring(os ~= nil) .. " io=" .. tostring(io ~= nil) .. " require=" .. tostring(require ~= nil))

-- ② 配置读写（宿主把它落到 <DataRoot>/<插件名>/config.json）—— 这也是我能从磁盘客观验证的落点
local cfg = ctx:get_config()
cfg.loaded_at = ctx:now().str
cfg.runs = (cfg.runs or 0) + 1
ctx:save_config(cfg)
ctx:log("[LuaSmoke] 已写入配置，本次是第 " .. tostring(cfg.runs) .. " 次加载")

-- ③ 终端命令
ctx:register_command("luasmoke", "Lua 冒烟命令：回显参数", function(raw, world)
  ctx:log("[LuaSmoke] 命令收到 raw=" .. tostring(raw) .. " world=" .. tostring(world))
end)

-- ④ 指标 + 状态胶囊（验证宿主会周期性回调 Lua）
local tick = 0
ctx:register_metric("Lua 心跳", "次", function() tick = tick + 1; return tick end)
ctx:register_status("Lua 状态", function() return cfg.runs and ("已加载 " .. cfg.runs .. " 次") or "就绪" end)

-- ⑤ 周期任务：每 5 秒记一次（验证计时器 + 后台线程执行）
ctx:every(5, function()
  -- ★ 把心跳写进配置：这样"计时器到底有没有在跑"可以从磁盘上客观验证（而不是只能看终端）
  cfg.ticks = (cfg.ticks or 0) + 1
  cfg.last_tick = ctx:now().str
  ctx:save_config(cfg)
  ctx:log("[LuaSmoke] 周期任务触发 " .. ctx:now().str .. "（第 " .. tostring(cfg.ticks) .. " 次）")
end)

-- ⑥ 声明式面板
ctx:register_panel("Lua 冒烟测试", function()
  return {
    { kind = "text", label = "加载次数", value = tostring(cfg.runs or 0) },
    { kind = "text", label = "上次加载", value = tostring(cfg.loaded_at or "-") },
    { kind = "select", id = "mode", label = "模式", options = { "间隔", "每天", "每周", "每月" }, value = cfg.mode or "间隔" },
    { kind = "number", id = "every", label = "间隔(分钟)", value = cfg.every or 30 },
    { kind = "text", id = "at", label = "执行时刻", value = cfg.at or "04:00" },
    { kind = "buttons", items = { { text = "保存", action = "save" }, { text = "立即触发", action = "run" } } },
  }
end)

ctx:on_action(function(action, values)
  ctx:log("[LuaSmoke] 面板动作 action=" .. tostring(action) .. " mode=" .. tostring(values.mode) .. " every=" .. tostring(values.every) .. " at=" .. tostring(values.at))
  if action == "save" then
    cfg.mode = values.mode; cfg.every = values.every; cfg.at = values.at
    ctx:save_config(cfg)
  end
  if _G then ctx:log("[LuaSmoke] 面板将重新求值") end
end)
