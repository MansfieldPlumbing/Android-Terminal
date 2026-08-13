using System;
using System.Runtime.InteropServices;
using System.Threading.Tasks;

namespace Terminal.Router;

internal static class Program
{
#if ANDROID
    [UnmanagedCallersOnly(EntryPoint = "terminal_router_main")]
    private static int AndroidMain()
    {
        return Run(Array.Empty<string>()).GetAwaiter().GetResult();
    }
#else
    public static async Task<int> Main(string[] args)
    {
        return await Run(args).ConfigureAwait(false);
    }
#endif

    private static async Task<int> Run(string[] args)
    {
        EndpointTransport? endpoint = null;
        try
        {
            endpoint = EndpointTransport.Acquire(args);
            var host = new RouterHost(endpoint);
            return await host.RunAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex.Message);
            return 1;
        }
        finally
        {
            endpoint?.Dispose();
        }
    }
}
