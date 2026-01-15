using System.Text;
using YiQiDong.Core;

namespace YiQiDong.Tools.DotTraceProfiler;

public class Agent : AbstractAgent
{
    public override void Init()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        base.Init();
        AddFunction(new Functions.Analyze());
    }
}
