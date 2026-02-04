using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using MediaBrowser.Model.Logging;

namespace StrmAssistant.Core
{
    /// <summary>
    /// 优化建议系统
    /// 根据性能和健康状态提供智能优化建议
    /// </summary>
    public class OptimizationAdvisor
    {
        private static OptimizationAdvisor _instance;
        private static readonly object _lock = new object();
        
        private readonly ILogger _logger;
        
        private OptimizationAdvisor(ILogger logger)
        {
            _logger = logger;
        }
        
        public static OptimizationAdvisor Instance
        {
            get
            {
                if (_instance == null)
                {
                    throw new InvalidOperationException("OptimizationAdvisor not initialized. Call Initialize() first.");
                }
                return _instance;
            }
        }
        
        public static void Initialize(ILogger logger)
        {
            if (_instance == null)
            {
                lock (_lock)
                {
                    if (_instance == null)
                    {
                        _instance = new OptimizationAdvisor(logger);
                    }
                }
            }
        }
        
        /// <summary>
        /// 分析当前状态并生成优化建议
        /// </summary>
        public List<OptimizationSuggestion> GetSuggestions()
        {
            var suggestions = new List<OptimizationSuggestion>();
            
            try
            {
                // 1. 检查FastReflection性能
                AnalyzeFastReflection(suggestions);
                
                // 2. 检查慢操作
                AnalyzeSlowOperations(suggestions);
                
                // 3. 检查内存使用
                AnalyzeMemoryUsage(suggestions);
                
                // 4. 检查健康状态
                AnalyzeHealthStatus(suggestions);
                
                // 5. 检查Harmony补丁状态
                AnalyzeHarmonyPatches(suggestions);
            }
            catch (Exception ex)
            {
                _logger.Error($"Failed to generate optimization suggestions: {ex.Message}");
            }
            
            return suggestions.OrderByDescending(s => s.Priority).ToList();
        }
        
        private void AnalyzeFastReflection(List<OptimizationSuggestion> suggestions)
        {
            try
            {
                var stats = FastReflection.Instance.GetPerformanceStats();
                
                // 缓存命中率低
                if (stats.TotalInvocations > 100 && stats.CacheHitRate < 50)
                {
                    suggestions.Add(new OptimizationSuggestion
                    {
                        Category = "Performance",
                        Priority = OptimizationPriority.High,
                        Title = "FastReflection缓存命中率过低",
                        Description = $"当前缓存命中率仅{stats.CacheHitRate:F2}%，建议检查是否存在动态方法调用",
                        Recommendation = "考虑预热常用方法或减少动态方法生成",
                        Impact = "可能导致性能下降50-100倍"
                    });
                }
                
                // 缓存大小异常
                if (stats.MethodCacheSize > 1000)
                {
                    suggestions.Add(new OptimizationSuggestion
                    {
                        Category = "Memory",
                        Priority = OptimizationPriority.Medium,
                        Title = "FastReflection缓存过大",
                        Description = $"方法缓存数量达到{stats.MethodCacheSize}，可能存在内存泄漏",
                        Recommendation = "检查是否有不必要的方法缓存，考虑定期清理",
                        Impact = "内存使用增加约" + (stats.MethodCacheSize * 5 / 1024) + "MB"
                    });
                }
            }
            catch
            {
                // FastReflection不可用
            }
        }
        
        private void AnalyzeSlowOperations(List<OptimizationSuggestion> suggestions)
        {
            try
            {
                var slowOps = PerformanceMonitor.Instance.GetSlowOperations(2000);
                
                if (slowOps.Length > 10)
                {
                    suggestions.Add(new OptimizationSuggestion
                    {
                        Category = "Performance",
                        Priority = OptimizationPriority.High,
                        Title = "检测到大量慢操作",
                        Description = $"有{slowOps.Length}个操作平均耗时超过2秒",
                        Recommendation = "使用PerformanceMonitor.GetSlowOperations()查看详情，考虑异步化或优化算法",
                        Impact = "用户体验受到严重影响"
                    });
                }
                else if (slowOps.Length > 5)
                {
                    suggestions.Add(new OptimizationSuggestion
                    {
                        Category = "Performance",
                        Priority = OptimizationPriority.Medium,
                        Title = "存在慢操作",
                        Description = $"有{slowOps.Length}个操作需要优化",
                        Recommendation = "检查慢操作日志，优化耗时的I/O或计算",
                        Impact = "可能影响响应速度"
                    });
                }
                
                // 检查特定慢操作
                foreach (var op in slowOps.Take(3))
                {
                    if (op.AverageMs > 5000)
                    {
                        suggestions.Add(new OptimizationSuggestion
                        {
                            Category = "Performance",
                            Priority = OptimizationPriority.High,
                            Title = $"极慢操作: {op.OperationName}",
                            Description = $"平均耗时{op.AverageMs:F2}ms，最大{op.MaxMs}ms",
                            Recommendation = "这个操作需要立即优化，考虑异步处理或缓存",
                            Impact = "严重影响用户体验"
                        });
                    }
                }
            }
            catch
            {
                // PerformanceMonitor不可用
            }
        }
        
        private void AnalyzeMemoryUsage(List<OptimizationSuggestion> suggestions)
        {
            try
            {
                var memoryMB = GC.GetTotalMemory(false) / 1024 / 1024;
                
                if (memoryMB > 500)
                {
                    suggestions.Add(new OptimizationSuggestion
                    {
                        Category = "Memory",
                        Priority = OptimizationPriority.High,
                        Title = "内存使用过高",
                        Description = $"当前内存使用{memoryMB}MB，超过建议值500MB",
                        Recommendation = "检查是否存在内存泄漏，考虑清理缓存或减少并发操作",
                        Impact = "可能导致系统不稳定或OOM"
                    });
                }
                else if (memoryMB > 300)
                {
                    suggestions.Add(new OptimizationSuggestion
                    {
                        Category = "Memory",
                        Priority = OptimizationPriority.Low,
                        Title = "内存使用偏高",
                        Description = $"当前内存使用{memoryMB}MB",
                        Recommendation = "可以考虑优化缓存策略",
                        Impact = "暂无明显影响"
                    });
                }
            }
            catch
            {
                // 获取内存信息失败
            }
        }
        
        private void AnalyzeHealthStatus(List<OptimizationSuggestion> suggestions)
        {
            try
            {
                var health = HealthCheck.Instance.PerformHealthCheck();
                
                if (health.OverallStatus == HealthStatus.Critical || health.OverallStatus == HealthStatus.Unhealthy)
                {
                    suggestions.Add(new OptimizationSuggestion
                    {
                        Category = "Health",
                        Priority = OptimizationPriority.Critical,
                        Title = "系统健康状态异常",
                        Description = $"当前状态: {health.OverallStatus}",
                        Recommendation = "使用HealthCheck.GenerateDiagnosticReport()查看详细诊断信息",
                        Impact = "核心功能可能不可用"
                    });
                    
                    // 添加具体问题
                    foreach (var issue in health.Issues.Take(3))
                    {
                        suggestions.Add(new OptimizationSuggestion
                        {
                            Category = "Health",
                            Priority = OptimizationPriority.High,
                            Title = "健康检查问题",
                            Description = issue,
                            Recommendation = "检查相关组件状态和日志",
                            Impact = "可能影响功能稳定性"
                        });
                    }
                }
                else if (health.OverallStatus == HealthStatus.Degraded)
                {
                    suggestions.Add(new OptimizationSuggestion
                    {
                        Category = "Health",
                        Priority = OptimizationPriority.Medium,
                        Title = "系统运行降级",
                        Description = $"当前状态: {health.OverallStatus}",
                        Recommendation = "检查降级组件，考虑优化或重启",
                        Impact = "部分功能可能受影响"
                    });
                }
            }
            catch
            {
                // HealthCheck不可用
            }
        }
        
        private void AnalyzeHarmonyPatches(List<OptimizationSuggestion> suggestions)
        {
            try
            {
                var patches = Mod.PatchManager.PatchTrackerList;
                var failedCorePatches = patches
                    .Where(p => p.IsCoreFeature && p.IsSupported && !p.IsRunning)
                    .ToList();
                
                if (failedCorePatches.Any())
                {
                    suggestions.Add(new OptimizationSuggestion
                    {
                        Category = "Compatibility",
                        Priority = OptimizationPriority.High,
                        Title = "核心补丁失败",
                        Description = $"{failedCorePatches.Count}个核心补丁未能运行",
                        Recommendation = "检查Emby版本兼容性，查看补丁错误日志",
                        Impact = "核心功能可能不可用"
                    });
                }
                
                var reflectionPatches = patches
                    .Where(p => p.IsRunning && p.FallbackPatchApproach == Mod.PatchApproach.Reflection)
                    .ToList();
                
                if (reflectionPatches.Count > patches.Count / 2)
                {
                    suggestions.Add(new OptimizationSuggestion
                    {
                        Category = "Performance",
                        Priority = OptimizationPriority.Low,
                        Title = "大量补丁使用Reflection模式",
                        Description = $"{reflectionPatches.Count}/{patches.Count}个补丁使用反射回退",
                        Recommendation = "这是正常的，FastReflection会自动优化性能",
                        Impact = "性能已通过FastReflection优化，影响较小"
                    });
                }
            }
            catch
            {
                // PatchManager不可用
            }
        }
        
        /// <summary>
        /// 生成优化建议报告
        /// </summary>
        public string GenerateReport()
        {
            var suggestions = GetSuggestions();
            
            if (suggestions.Count == 0)
            {
                return "✓ 系统运行良好，暂无优化建议";
            }
            
            var sb = new StringBuilder();
            sb.AppendLine("=== 优化建议报告 ===");
            sb.AppendLine($"生成时间: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            sb.AppendLine($"建议数量: {suggestions.Count}");
            sb.AppendLine();
            
            var groups = suggestions.GroupBy(s => s.Priority).OrderByDescending(g => g.Key);
            
            foreach (var group in groups)
            {
                sb.AppendLine($"【{group.Key} 优先级】");
                foreach (var suggestion in group)
                {
                    sb.AppendLine($"• {suggestion.Title}");
                    sb.AppendLine($"  类别: {suggestion.Category}");
                    sb.AppendLine($"  描述: {suggestion.Description}");
                    sb.AppendLine($"  建议: {suggestion.Recommendation}");
                    sb.AppendLine($"  影响: {suggestion.Impact}");
                    sb.AppendLine();
                }
            }
            
            sb.AppendLine("===================");
            return sb.ToString();
        }
    }
    
    /// <summary>
    /// 优化建议
    /// </summary>
    public class OptimizationSuggestion
    {
        public string Category { get; set; }
        public OptimizationPriority Priority { get; set; }
        public string Title { get; set; }
        public string Description { get; set; }
        public string Recommendation { get; set; }
        public string Impact { get; set; }
    }
    
    /// <summary>
    /// 优化优先级
    /// </summary>
    public enum OptimizationPriority
    {
        Low = 1,
        Medium = 2,
        High = 3,
        Critical = 4
    }
}
