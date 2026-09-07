namespace MadTOM.Models;

public sealed class ProcessInfoModel
{
    public int Pid { get; set; }
    public string Name { get; set; } = string.Empty;
    public string User { get; set; } = string.Empty;
    public int Threads { get; set; }
    public double Cpu { get; set; }
    public string Mem { get; set; } = string.Empty;
}

