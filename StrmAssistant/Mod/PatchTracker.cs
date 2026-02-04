using System;
using System.Collections.Generic;

namespace StrmAssistant.Mod
{
    public enum PatchApproach
    {
        None,
        Reflection,
        Harmony
    }
    
    public enum PatchStatus
    {
        /// <summary>
        /// 未初始化
        /// </summary>
        NotInitialized,
        
        /// <summary>
        /// 初始化中
        /// </summary>
        Initializing,
        
        /// <summary>
        /// 初始化成功
        /// </summary>
        Initialized,
        
        /// <summary>
        /// 已应用
        /// </summary>
        Applied,
        
        /// <summary>
        /// 初始化失败
        /// </summary>
        Failed,
        
        /// <summary>
        /// 不支持
        /// </summary>
        NotSupported
    }

    public class PatchTracker
    {
        public PatchTracker(Type patchType, PatchApproach defaultApproach)
        {
            PatchType = patchType;
            DefaultPatchApproach = defaultApproach;
            FallbackPatchApproach = defaultApproach;
            Status = PatchStatus.NotInitialized;
            InitializedAt = null;
            ErrorMessages = new List<string>();

            PatchManager.PatchTrackerList.Add(this);
        }

        public Type PatchType { get; set; }

        public PatchApproach DefaultPatchApproach { get; }

        public PatchApproach FallbackPatchApproach { get; set; }

        public bool IsSupported { get; set; } = true;
        
        /// <summary>
        /// 补丁状态
        /// </summary>
        public PatchStatus Status { get; set; }
        
        /// <summary>
        /// 初始化时间
        /// </summary>
        public DateTime? InitializedAt { get; set; }
        
        /// <summary>
        /// 错误消息列表
        /// </summary>
        public List<string> ErrorMessages { get; }
        
        /// <summary>
        /// 是否为核心补丁（失败时会影响整体功能）
        /// </summary>
        public bool IsCoreFeature { get; set; } = true;
        
        /// <summary>
        /// 功能描述
        /// </summary>
        public string Description { get; set; }
        
        /// <summary>
        /// 添加错误消息
        /// </summary>
        public void AddError(string message)
        {
            if (!string.IsNullOrEmpty(message))
            {
                ErrorMessages.Add($"[{DateTime.Now:HH:mm:ss}] {message}");
            }
        }
        
        /// <summary>
        /// 清空错误消息
        /// </summary>
        public void ClearErrors()
        {
            ErrorMessages.Clear();
        }
        
        /// <summary>
        /// 是否有错误
        /// </summary>
        public bool HasErrors => ErrorMessages.Count > 0;
        
        /// <summary>
        /// 是否成功运行（使用Harmony或Reflection）
        /// </summary>
        public bool IsRunning => FallbackPatchApproach != PatchApproach.None;
        
        /// <summary>
        /// 获取状态摘要
        /// </summary>
        public string GetStatusSummary()
        {
            var approach = FallbackPatchApproach == DefaultPatchApproach 
                ? $"{FallbackPatchApproach}" 
                : $"{DefaultPatchApproach} → {FallbackPatchApproach}";
                
            return $"{PatchType.Name}: {Status} ({approach})";
        }
    }
}
