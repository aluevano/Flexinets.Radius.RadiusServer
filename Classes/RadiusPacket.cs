﻿using log4net;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Security.Cryptography;
using System.Text;

namespace Flexinets.Radius
{
    /// <summary>
    /// This class encapsulates a Radius packet and presents it in a more readable form
    /// </summary>
    public class RadiusPacket : IRadiusPacket
    {
        private static readonly ILog _log = LogManager.GetLogger(typeof(RadiusPacket));

        public PacketCode Code
        {
            get;
            private set;
        }
        public Byte Identifier
        {
            get;
            private set;
        }
        public Byte[] Authenticator
        {
            get;
            private set;
        }
        public IDictionary<String, List<Object>> Attributes
        {
            get;
            set;
        }
        public Byte[] SharedSecret
        {
            get;
            private set;
        }


        /// <summary>
        /// Parses the udp raw packet and returns a more easily readable IRadiusPacket
        /// </summary>
        /// <param name="packetBytes"></param>
        /// <param name="dictionary"></param>
        /// <param name="sharedSecret"></param>
        public static IRadiusPacket ParseRawPacket(Byte[] packetBytes, RadiusDictionary dictionary, Byte[] sharedSecret)
        {
            // Check the packet length and make sure its valid
            Array.Reverse(packetBytes, 2, 2);
            var packetLength = BitConverter.ToUInt16(packetBytes, 2);
            if (packetBytes.Length != packetLength)
            {
                var message = $"Packet length does not match, expected: {packetLength}, actual: {packetBytes.Length}";
                _log.ErrorFormat(message);
                throw new InvalidOperationException(message);
            }

            var radiusPacket = new RadiusPacket
            {
                SharedSecret = sharedSecret,
                Attributes = new Dictionary<String, List<object>>(),
                Identifier = packetBytes[1],
                Code = (PacketCode)packetBytes[0],
                Authenticator = new Byte[16]
            };

            Buffer.BlockCopy(packetBytes, 4, radiusPacket.Authenticator, 0, 16);

            // The rest are attribute value pairs
            Int16 i = 20;
            while (i < packetBytes.Length)
            {
                var typecode = packetBytes[i];
                var length = packetBytes[i + 1];

                if (i + length > packetLength)
                {
                    throw new ArgumentOutOfRangeException("Go home roamserver, youre drunk");
                }
                var contentBytes = new Byte[length - 2];
                Buffer.BlockCopy(packetBytes, i + 2, contentBytes, 0, length - 2);

                try
                {
                    if (typecode == 26)
                    {
                        var vsa = new VendorSpecificAttribute(contentBytes);
                        var attributeDefinition = dictionary.VendorAttributes.FirstOrDefault(o => o.VendorId == vsa.VendorId && o.Code == vsa.VendorCode);
                        if (attributeDefinition == null)
                        {
                            _log.Debug($"Unknown vsa: {vsa.VendorId}:{vsa.VendorCode}");
                        }
                        else
                        {
                            try
                            {
                                var content = ParseContentBytes(vsa.Value, attributeDefinition.Type, typecode, radiusPacket.Authenticator, radiusPacket.SharedSecret);
                                if (!radiusPacket.Attributes.ContainsKey(attributeDefinition.Name))
                                {
                                    radiusPacket.Attributes.Add(attributeDefinition.Name, new List<object>());
                                }
                                radiusPacket.Attributes[attributeDefinition.Name].Add(content);
                            }
                            catch (Exception ex)
                            {
                                _log.Error($"Something went wrong with vsa {attributeDefinition.Name}", ex);
                            }
                        }
                    }
                    else
                    {
                        var attributeDefinition = dictionary.Attributes[typecode];
                        try
                        {
                            var content = ParseContentBytes(contentBytes, attributeDefinition.Type, typecode, radiusPacket.Authenticator, radiusPacket.SharedSecret);
                            if (!radiusPacket.Attributes.ContainsKey(attributeDefinition.Name))
                            {
                                radiusPacket.Attributes.Add(attributeDefinition.Name, new List<object>());
                            }
                            radiusPacket.Attributes[attributeDefinition.Name].Add(content);
                        }
                        catch (Exception ex)
                        {
                            _log.Error($"Something went wrong with vsa {attributeDefinition.Name}", ex);
                        }
                    }
                }
                catch (KeyNotFoundException)
                {
                    _log.Warn($"Attribute {typecode} not found in dictionary");
                }
                catch (Exception ex)
                {
                    _log.Error($"Something went wrong parsing attribute {typecode}", ex);
                }

                i += length;
            }

            return radiusPacket;
        }


        /// <summary>
        /// Parses the content and returns an object of proper type
        /// </summary>
        /// <param name="contentBytes"></param>
        /// <param name="type"></param>
        /// <param name="code"></param>
        /// <param name="authenticator"></param>
        /// <param name="sharedSecret"></param>
        /// <returns></returns>
        private static Object ParseContentBytes(Byte[] contentBytes, String type, UInt32 code, Byte[] authenticator, Byte[] sharedSecret)
        {
            switch (type)
            {
                case "string":
                    return Encoding.UTF8.GetString(contentBytes);

                case "tagged-string":
                    return Encoding.UTF8.GetString(contentBytes);

                case "binary":
                    // If this is a password attribute it must be decrypted
                    if (code == 2)
                    {
                        return RadiusPassword.Decrypt(sharedSecret, authenticator, contentBytes);
                    }
                    // Otherwise just dump the bytes into the attribute
                    else
                    {
                        return contentBytes;
                    }

                case "integer":
                    return BitConverter.ToUInt32(contentBytes.Reverse().ToArray(), 0);

                case "tagged-integer":
                    return BitConverter.ToUInt32(contentBytes.Reverse().ToArray(), 0);

                case "ipaddr":
                    return new IPAddress(contentBytes);

                default:
                    return null;
            }
        }


        /// <summary>
        /// Creates a response packet with authenticator, identifier, secret etc set
        /// </summary>
        /// <param name="responseCode"></param>
        /// <returns></returns>
        public IRadiusPacket CreateResponsePacket(PacketCode responseCode)
        {
            return new RadiusPacket
            {
                Attributes = new Dictionary<String, List<object>>(),
                Code = responseCode,
                SharedSecret = SharedSecret,
                Identifier = Identifier,
                Authenticator = Authenticator,
            };
        }


        /// <summary>
        /// Gets a single attribute value with name cast to type
        /// Throws an exception if multiple attributes with the same name are found
        /// </summary>
        /// <typeparam name="T"></typeparam>
        /// <param name="name"></param>
        /// <returns></returns>
        public T GetAttribute<T>(String name)
        {
            if (Attributes.ContainsKey(name))
            {
                return (T)Attributes[name].Single();
            }

            return default(T);
        }


        /// <summary>
        /// Gets multiple attribute values with the same name cast to type
        /// </summary>
        /// <typeparam name="T"></typeparam>
        /// <param name="name"></param>
        /// <returns></returns>
        public List<T> GetAttributes<T>(String name)
        {
            if (Attributes.ContainsKey(name))
            {
                return Attributes[name].Cast<T>().ToList();
            }
            return new List<T>();
        }




        /// <summary>
        /// Add attribute to packet
        /// </summary>
        /// <param name="name"></param>
        /// <param name="value"></param>
        public void AddAttribute(String name, String value)
        {
            AddAttributeObject(name, value);
        }
        public void AddAttribute(String name, UInt32 value)
        {
            AddAttributeObject(name, value);
        }
        public void AddAttribute(String name, IPAddress value)
        {
            AddAttributeObject(name, value);
        }
        public void AddAttribute(String name, Byte[] value)
        {
            AddAttributeObject(name, value);
        }

        private void AddAttributeObject(string name, object value)
        {
            if (!Attributes.ContainsKey(name))
            {
                Attributes.Add(name, new List<object>());
            }
            Attributes[name].Add(value);
        }


        /// <summary>
        /// Validates a message authenticator if one exists in the packet
        /// Message-Authenticator = HMAC-MD5 (Type, Identifier, Length, Request Authenticator, Attributes)
        /// The HMAC-MD5 function takes in two arguments:
        /// The payload of the packet, which includes the 16 byte Message-Authenticator field filled with zeros
        /// The shared secret
        /// https://www.ietf.org/rfc/rfc2869.txt
        /// </summary>
        /// <returns></returns>
        public static String CalculateMessageAuthenticator(IRadiusPacket packet, RadiusDictionary dictionary)
        {            
            var checkPacket = ParseRawPacket(packet.GetBytes(dictionary), dictionary, packet.SharedSecret);    // This is a bit daft, but we dont want side effects do we...
            checkPacket.Attributes["Message-Authenticator"][0] = new Byte[16];

            var bytes = checkPacket.GetBytes(dictionary);

            using (var md5 = new HMACMD5(checkPacket.SharedSecret))
            {
                return Utils.ByteArrayToString(md5.ComputeHash(bytes));
            }
        }


        /// <summary>
        /// Get the raw packet bytes
        /// </summary>
        /// <returns></returns>
        public Byte[] GetBytes(RadiusDictionary dictionary)
        {
            var packetbytes = new Byte[20]; // Should be 20 + AVPs...
            packetbytes[0] = (Byte)Code;
            packetbytes[1] = Identifier;

            foreach (var attribute in Attributes)
            {
                // todo add logic to check attribute object type matches type in dictionary?
                foreach (var value in attribute.Value)
                {
                    var contentBytes = GetAttributeValueBytes(value);
                    var headerBytes = new Byte[0];

                    // Figure out what kind of attribute this is
                    var attributeType = dictionary.Attributes.SingleOrDefault(o => o.Value.Name == attribute.Key);
                    if (dictionary.Attributes.ContainsValue(attributeType.Value))
                    {
                        headerBytes = new Byte[2];
                        headerBytes[0] = attributeType.Value.Code;
                    }
                    else
                    {
                        // Maybe this is a vendor attribute?
                        var vendorAttributeType = dictionary.VendorAttributes.SingleOrDefault(o => o.Name == attribute.Key);
                        if (vendorAttributeType != null)
                        {
                            headerBytes = new Byte[8];
                            headerBytes[0] = 26; // VSA type

                            var vendorId = BitConverter.GetBytes(vendorAttributeType.VendorId);
                            Array.Reverse(vendorId);
                            Buffer.BlockCopy(vendorId, 0, headerBytes, 2, 4);
                            headerBytes[6] = (Byte)vendorAttributeType.Code;
                            headerBytes[7] = (Byte)(2 + contentBytes.Length);  // length of the vsa part
                        }
                        else
                        {
                            _log.Debug($"Ignoring unknown attribute {attribute.Key}");
                        }
                    }

                    var attributeBytes = new Byte[headerBytes.Length + contentBytes.Length];
                    Buffer.BlockCopy(headerBytes, 0, attributeBytes, 0, headerBytes.Length);
                    Buffer.BlockCopy(contentBytes, 0, attributeBytes, headerBytes.Length, contentBytes.Length);
                    attributeBytes[1] = (Byte)attributeBytes.Length;

                    // Add attribute to packet
                    Array.Resize(ref packetbytes, packetbytes.Length + attributeBytes.Length);
                    Buffer.BlockCopy(attributeBytes, 0, packetbytes, packetbytes.Length - attributeBytes.Length, attributeBytes.Length);
                }
            }

            // Note the order of the bytes...
            var responselengthbytes = BitConverter.GetBytes(packetbytes.Length);
            packetbytes[2] = responselengthbytes[1];
            packetbytes[3] = responselengthbytes[0];

            Buffer.BlockCopy(Authenticator, 0, packetbytes, 4, 16);

            return packetbytes;
        }


        /// <summary>
        /// Gets the byte representation of an attribute object
        /// </summary>
        /// <param name="value"></param>
        /// <returns></returns>
        private static Byte[] GetAttributeValueBytes(Object value)
        {
            byte[] contentBytes;
            if (value.GetType() == typeof(String))
            {
                contentBytes = Encoding.Default.GetBytes((String)value);
            }
            else if (value.GetType() == typeof(UInt32))
            {
                contentBytes = BitConverter.GetBytes((UInt32)value);
                Array.Reverse(contentBytes);
            }
            else if (value.GetType() == typeof(Byte[]))
            {
                contentBytes = (Byte[])value;
            }
            else if (value.GetType() == typeof(IPAddress))
            {
                contentBytes = ((IPAddress)value).GetAddressBytes();
            }
            else
            {
                throw new NotImplementedException();
            }

            return contentBytes;
        }
    }
}