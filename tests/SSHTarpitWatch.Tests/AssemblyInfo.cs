using Microsoft.VisualStudio.TestTools.UnitTesting;

// 测试共享进程级全局状态（Console 重定向、Stats 计数器、日志）——强制串行，避免测试相互干扰。
[assembly: DoNotParallelize]
