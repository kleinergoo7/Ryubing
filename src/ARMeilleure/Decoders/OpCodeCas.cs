namespace ARMeilleure.Decoders
{
    class OpCodeCas : OpCodeMem
    {
        public int Rs { get; }

        public new static OpCode Create(InstDescriptor inst, ulong address, int opCode) => new OpCodeCas(inst, address, opCode);

        public OpCodeCas(InstDescriptor inst, ulong address, int opCode) : base(inst, address, opCode)
        {
            Rs = (opCode >> 16) & 0x1f;
        }
    }
}
