namespace PreyToLive.CustomLauncherBackgrounds;

using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.DI;

/// <summary>
/// Runs at PostLoad - after the core routers (including the SPT_Data image import) are
/// registered - so the override of the /files/launcher/bg route always wins.
/// </summary>
[Injectable(InjectionType.Singleton, int.MaxValue, TypePriority = OnLoadOrder.PostLoad)]
public class BackgroundOnLoad(BackgroundSelectionService service) : IOnLoad
{
    public Task OnLoadAsync(CancellationToken cancellationToken)
    {
        service.Apply();
        return Task.CompletedTask;
    }
}
