using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using IPFS.Services;

namespace IPFS.Views;

/// <summary>把 Slider 的指针事件接到 <see cref="ScrubController"/>：按下冻结、松手定位。</summary>
public static class SliderScrubExtensions
{
    static readonly AttachedProperty<ScrubController?> ControllerProperty =
        AvaloniaProperty.RegisterAttached<Attach, Slider, ScrubController?>("ScrubController");
    static readonly AttachedProperty<bool> WiredProperty =
        AvaloniaProperty.RegisterAttached<Attach, Slider, bool>("ScrubWired");

    /// <summary>附属属性的宿主类型；静态类不能作泛型参数，单独开一个空类</summary>
    sealed class Attach
    {

    }

    /// <summary>可重复调用（如 DataContext 切换），事件只接一次，控制器更新为最新实例</summary>
    public static void AttachScrub(this Slider slider, ScrubController controller)
    {
        slider.SetValue(ControllerProperty, controller);
        if (slider.GetValue(WiredProperty)) return;
        slider.SetValue(WiredProperty, true);

        //Slider 内部会把指针事件标记为已处理，必须在隧道阶段并接收已处理事件
        slider.AddHandler(InputElement.PointerPressedEvent, OnPressed, RoutingStrategies.Tunnel, handledEventsToo: true);
        slider.AddHandler(InputElement.PointerReleasedEvent, OnReleased, RoutingStrategies.Tunnel | RoutingStrategies.Bubble, handledEventsToo: true);
        slider.AddHandler(InputElement.PointerCaptureLostEvent, OnCaptureLost, RoutingStrategies.Bubble, handledEventsToo: true);
    }

    static void OnPressed(object? sender, PointerPressedEventArgs e)
        => GetController(sender)?.Begin();

    static void OnReleased(object? sender, PointerReleasedEventArgs e)
        => EndScrub(sender);

    /// <summary>指针在控件外释放时也能正确收尾</summary>
    static void OnCaptureLost(object? sender, PointerCaptureLostEventArgs e)
        => EndScrub(sender);

    static void EndScrub(object? sender)
    {
        if (sender is Slider slider)
            GetController(slider)?.End(slider.Value);
    }

    static ScrubController? GetController(object? sender)
        => sender is Slider slider ? slider.GetValue(ControllerProperty) : null;
}
