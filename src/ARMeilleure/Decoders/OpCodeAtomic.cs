namespace ARMeilleure.Decoders
{
    class OpCodeAtomic : OpCodeMem
    {
        public int Rs { get; }
        public int Op { get; }

        public new static OpCode Create(InstDescriptor inst, ulong address, int opCode) => new OpCodeAtomic(inst, address, opCode);

        public OpCodeAtomic(InstDescriptor inst, ulong address, int opCode) : base(inst, address, opCode)
        {
            Rs = (opCode >> 16) & 0x1f;
            Op = (opCode >> 12) & 0xf;
        }
    }
}
