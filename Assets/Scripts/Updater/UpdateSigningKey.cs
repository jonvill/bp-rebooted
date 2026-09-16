namespace BPRE.Updater
{
	/// <summary>
	/// Public half of the release signing key. Updates are only installed if their manifest
	/// is signed with the matching private key, which never leaves the release machine
	/// (%USERPROFILE%\.bpre\update-signing-key.xml, see Tools/Release.ps1).
	/// </summary>
	public static class UpdateSigningKey
	{
		public const string PublicKeyXml = "<RSAKeyValue><Modulus>x+rIO2WG5PlOFBTT+v/6VrA5vT9e8NgbqTlsGIGC0lu7R9Z9L/3bUIFpK3Ki50hA/wcA2mlJ1tSt/w0ne5gkSn47zuxV1wXP5DhPJHN3YXJ0yusy7py91XZOpeoSpj5U5mlDGyrDnMxcALerr9DFG1Wfr5jDno8eGpF3lwzuQlLt5HrAk3IIZx6jMrWtxHKPXexOxuvvhrQVzAqGN7/8m8rjwu8OY0EWY0rW5z+MAVmfO0jliczFgb4FY59grhoDZH5cjq/pYhrTQ+qiNYkIBFvl5oFd3pccRlFmguNMlYCnqXfs6WwQQL2Fo4zud2X4SQZ6H99o79MHsVp8KigHcOPtdgiWHUbusj3fmbphZXcGMCBOCfdIuFS2P93rMn+5bm+GMTxQcvgXVPmTiCHH10Vogv4pAIxyDcT51rwagtpfC/mjQ8ofSNkpXDovW1Ss93YnZ8L36ddsmABeqNq0Llef0orrwXXLp1fR36TQiBre+t21ECMQHlNVmbsNLDYN</Modulus><Exponent>AQAB</Exponent></RSAKeyValue>";
	}
}
