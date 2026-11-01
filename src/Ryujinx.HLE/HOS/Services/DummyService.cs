namespace Ryujinx.HLE.HOS.Services
{
    class DummyService : IpcService
    {
        public string ServiceName { get; set; }

        public DummyService(string serviceName) : base(registerTipc: true)
        {
            ServiceName = serviceName;
        }
    }
}
