using System;

namespace StrmAssistant.Core
{
    /// <summary>
    /// Emby API版本枚举，用于标识不同版本的API行为
    /// </summary>
    public enum EmbyApiVersion
    {
        /// <summary>
        /// 未知版本
        /// </summary>
        Unknown = 0,
        
        /// <summary>
        /// 4.8.x 基础版本
        /// </summary>
        V4_8_0 = 4800,
        
        /// <summary>
        /// 4.8.3.x 版本
        /// </summary>
        V4_8_3 = 4830,
        
        /// <summary>
        /// 4.9.0.x 早期版本
        /// </summary>
        V4_9_0 = 4900,
        
        /// <summary>
        /// 4.9.1.0-79 中期版本
        /// </summary>
        V4_9_1 = 4910,
        
        /// <summary>
        /// 4.9.1.80+ 稳定版本
        /// </summary>
        V4_9_1_80 = 49180,
        
        /// <summary>
        /// 4.9.1.90+ 最新稳定版本（当前推荐）
        /// </summary>
        V4_9_1_90 = 49190,
        
        /// <summary>
        /// 4.9.2.x 未来版本
        /// </summary>
        V4_9_2 = 4920,
        
        /// <summary>
        /// 4.10.x 未来主要版本
        /// </summary>
        V4_10_0 = 41000
    }
}
