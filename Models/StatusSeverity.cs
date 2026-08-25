namespace SimToAutoWirte.Models;

/// <summary>
/// 状态的严重级别，用于驱动界面上状态指示灯的语义颜色。
/// </summary>
public enum StatusSeverity
{
    /// <summary>就绪或成功完成。</summary>
    Ready,

    /// <summary>正在倒计时或正在输入。</summary>
    Busy,

    /// <summary>操作被拒绝或被用户中止，但并非故障。</summary>
    Warning,

    /// <summary>热键注册失败或输入过程中出现异常。</summary>
    Error,
}
