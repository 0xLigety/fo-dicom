﻿// Copyright (c) 2012-2015 fo-dicom contributors.
// Licensed under the Microsoft Public License (MS-PL).

namespace Dicom.Network
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Linq;
    using System.Text;
    using System.Threading.Tasks;

    using Dicom.Imaging.Codec;
    using Dicom.IO;
    using Dicom.IO.Reader;
    using Dicom.IO.Writer;
    using Dicom.Log;

    /// <summary>
    /// Base class for DICOM network services.
    /// </summary>
    public abstract class DicomService
    {
        #region FIELDS

        private readonly Stream _network;

        private readonly object _lock;

        private volatile bool _writing;

        private volatile bool _sending;

        private readonly Queue<PDU> _pduQueue;

        private readonly Queue<DicomMessage> _msgQueue;

        private readonly List<DicomRequest> _pending;

        private DicomMessage _dimse;

        private Stream _dimseStream;

        private IFileReference _dimseStreamFile;

        private int _readLength;

        private bool _isConnected;

        private readonly Encoding _fallbackEncoding;

        #endregion

        #region CONSTRUCTORS

        /// <summary>
        /// Initializes a new instance of the <see cref="DicomService"/> class.
        /// </summary>
        /// <param name="stream">Network stream.</param>
        /// <param name="log">Logger</param>
        protected DicomService(Stream stream, Logger log)
            : this(stream, DicomEncoding.Default, log)
        {
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="DicomService"/> class.
        /// </summary>
        /// <param name="stream">Network stream.</param>
        /// <param name="fallbackEncoding">Fallback encoding.</param>
        /// <param name="log">Logger</param>
        protected DicomService(Stream stream, Encoding fallbackEncoding, Logger log)
        {
            if (fallbackEncoding == null)
            {
                throw new ArgumentNullException("fallbackEncoding");
            }

            _network = stream;
            _lock = new object();
            _pduQueue = new Queue<PDU>();
            MaximumPDUsInQueue = 16;
            _msgQueue = new Queue<DicomMessage>();
            _pending = new List<DicomRequest>();
            _isConnected = true;
            _fallbackEncoding = fallbackEncoding;
            Logger = log ?? LogManager.GetLogger("Dicom.Network");
            Options = DicomServiceOptions.Default;

            this.ReadAndProcessPDUs();
        }

        #endregion

        #region PROPERTIES

        /// <summary>
        /// Gets or sets the logger.
        /// </summary>
        public Logger Logger { get; set; }

        /// <summary>
        /// Gets or sets the DICOM service options.
        /// </summary>
        public DicomServiceOptions Options { get; set; }

        /// <summary>
        /// Gets or sets the log ID.
        /// </summary>
        private string LogID { get; set; }

        /// <summary>
        /// Gets or sets a user state associated with the service.
        /// </summary>
        public object UserState { get; set; }

        /// <summary>
        /// Gets the DICOM association.
        /// </summary>
        public DicomAssociation Association { get; internal set; }

        /// <summary>
        /// Gets whether or not the service is connected.
        /// </summary>
        public bool IsConnected
        {
            get
            {
                return _isConnected;
            }
        }

        /// <summary>
        /// Gets whether or not the send queue is empty.
        /// </summary>
        public bool IsSendQueueEmpty
        {
            get
            {
                lock (_lock) return _pending.Count == 0;
            }
        }

        /// <summary>
        /// Gets or sets the maximum number of PDUs in queue.
        /// </summary>
        public int MaximumPDUsInQueue { get; set; }

        #endregion

        #region METHODS

        /// <summary>
        /// Send request from service.
        /// </summary>
        /// <param name="request">Request to send.</param>
        public virtual void SendRequest(DicomRequest request)
        {
            SendMessage(request);
        }

        /// <summary>
        /// Send response from service.
        /// </summary>
        /// <param name="response">Response to send.</param>
        protected void SendResponse(DicomResponse response)
        {
            SendMessage(response);
        }

        /// <summary>
        /// The purpose of this method is to return the Stream that a SopInstance received
        /// via CStoreSCP will be written to.  This default implementation creates a temporary
        /// file and returns a FileStream on top of it.  Child classes can override this to write
        /// to another stream and avoid the I/O associated with the temporary file if so desired.
        /// Beware that some SopInstances can be very large so using a MemoryStream() could cause
        /// out of memory situations.
        /// </summary>
        /// <param name="file">A DicomFile with FileMetaInfo populated.</param>
        /// <returns>The stream to write the SopInstance to.</returns>
        protected virtual void CreateCStoreReceiveStream(DicomFile file)
        {
            _dimseStreamFile = TemporaryFile.Create();

            _dimseStream = _dimseStreamFile.Open();
            file.Save(_dimseStream);
            _dimseStream.Seek(0, SeekOrigin.End);

        }

        /// <summary>
        /// The purpose of this method is to create a DicomFile for the SopInstance received via
        /// CStoreSCP to pass to the IDicomCStoreProvider.OnCStoreRequest method for processing.
        /// This default implementation will return a DicomFile if the stream created by
        /// CreateCStoreReceiveStream() is seekable or null if it is not.  Child classes that 
        /// override CreateCStoreReceiveStream may also want override this to return a DicomFile 
        /// for unseekable streams or to do cleanup related to receiving that specific instance.  
        /// </summary>
        /// <returns>The DicomFile or null if the stream is not seekable.</returns>
        protected virtual DicomFile GetCStoreDicomFile()
        {
            if (_dimseStreamFile != null)
            {
                if (_dimseStream != null) _dimseStream.Dispose();
                return DicomFile.Open(_dimseStreamFile, _fallbackEncoding);
            }

            if (_dimseStream != null && _dimseStream.CanSeek)
            {
                _dimseStream.Seek(0, SeekOrigin.Begin);
                return DicomFile.Open(_dimseStream, _fallbackEncoding);
            }

            return null;
        }

        /// <summary>
        /// Asynchronously send single PDU.
        /// </summary>
        /// <param name="pdu">PDU to send.</param>
        /// <returns>Awaitable task.</returns>
        protected async Task SendPDUAsync(PDU pdu)
        {
            while (this._pduQueue.Count >= this.MaximumPDUsInQueue)
            {
                await Task.Delay(10).ConfigureAwait(false);
            }

            lock (this._lock)
            {
                this._pduQueue.Enqueue(pdu);
            }

            await this.SendNextPDUAsync().ConfigureAwait(false);
        }

        private void CloseConnection(Exception exception)
        {
            if (!_isConnected) return;

            _isConnected = false;
            try
            {
                _network.Dispose();
            }
            catch
            {
            }

            if (exception != null) Logger.Error("Connection closed with error: {@error}", exception);
            else Logger.Info("Connection closed");

            if (this is IDicomServiceProvider) (this as IDicomServiceProvider).OnConnectionClosed(exception);
            else if (this is IDicomServiceUser) (this as IDicomServiceUser).OnConnectionClosed(exception);
        }

        private async void ReadAndProcessPDUs()
        {
            try
            {
                while (this.IsConnected)
                {
                    // Read PDU header
                    _readLength = 6;

                    var buffer = new byte[6];
                    var count = await this._network.ReadAsync(buffer, 0, 6).ConfigureAwait(false);

                    do
                    {
                        if (count == 0)
                        {
                            // disconnected
                            this.CloseConnection(null);
                            return;
                        }

                        this._readLength -= count;
                        if (this._readLength > 0)
                        {
                            count =
                                await
                                this._network.ReadAsync(buffer, 6 - this._readLength, this._readLength)
                                    .ConfigureAwait(false);
                        }
                    }
                    while (this._readLength > 0);

                    var length = BitConverter.ToInt32(buffer, 2);
                    length = Endian.Swap(length);

                    this._readLength = length;

                    Array.Resize(ref buffer, length + 6);

                    count = await this._network.ReadAsync(buffer, 6, length).ConfigureAwait(false);

                    // Read PDU
                    do
                    {
                        if (count == 0)
                        {
                            // disconnected
                            this.CloseConnection(null);
                            return;
                        }

                        this._readLength -= count;
                        if (this._readLength > 0)
                        {
                            count =
                                await
                                this._network.ReadAsync(buffer, buffer.Length - this._readLength, this._readLength)
                                    .ConfigureAwait(false);
                        }
                    }
                    while (this._readLength > 0);

                    var raw = new RawPDU(buffer);

                    switch (raw.Type)
                    {
                        case 0x01:
                            {
                                Association = new DicomAssociation();
                                var pdu = new AAssociateRQ(Association);
                                pdu.Read(raw);
                                LogID = Association.CallingAE;
                                if (Options.UseRemoteAEForLogName) Logger = LogManager.GetLogger(LogID);
                                Logger.Info(
                                    "{callingAE} <- Association request:\n{association}",
                                    LogID,
                                    Association.ToString());
                                if (this is IDicomServiceProvider) (this as IDicomServiceProvider).OnReceiveAssociationRequest(Association);
                                break;
                            }
                        case 0x02:
                            {
                                var pdu = new AAssociateAC(Association);
                                pdu.Read(raw);
                                LogID = Association.CalledAE;
                                Logger.Info(
                                    "{calledAE} <- Association accept:\n{assocation}",
                                    LogID,
                                    Association.ToString());
                                if (this is IDicomServiceUser) (this as IDicomServiceUser).OnReceiveAssociationAccept(Association);
                                break;
                            }
                        case 0x03:
                            {
                                var pdu = new AAssociateRJ();
                                pdu.Read(raw);
                                Logger.Info(
                                    "{logId} <- Association reject [result: {pduResult}; source: {pduSource}; reason: {pduReason}]",
                                    LogID,
                                    pdu.Result,
                                    pdu.Source,
                                    pdu.Reason);
                                if (this is IDicomServiceUser)
                                    (this as IDicomServiceUser).OnReceiveAssociationReject(
                                        pdu.Result,
                                        pdu.Source,
                                        pdu.Reason);
                                break;
                            }
                        case 0x04:
                            {
                                var pdu = new PDataTF();
                                pdu.Read(raw);
                                if (Options.LogDataPDUs) Logger.Info("{logId} <- {@pdu}", LogID, pdu);
                                await this.ProcessPDataTFAsync(pdu).ConfigureAwait(false);
                                break;
                            }
                        case 0x05:
                            {
                                var pdu = new AReleaseRQ();
                                pdu.Read(raw);
                                Logger.Info("{logId} <- Association release request", LogID);
                                if (this is IDicomServiceProvider) (this as IDicomServiceProvider).OnReceiveAssociationReleaseRequest();
                                break;
                            }
                        case 0x06:
                            {
                                var pdu = new AReleaseRP();
                                pdu.Read(raw);
                                Logger.Info("{logId} <- Association release response", LogID);
                                if (this is IDicomServiceUser) (this as IDicomServiceUser).OnReceiveAssociationReleaseResponse();
                                CloseConnection(null);
                                return;
                            }
                        case 0x07:
                            {
                                var pdu = new AAbort();
                                pdu.Read(raw);
                                Logger.Info(
                                    "{logId} <- Abort: {pduSource} - {pduReason}",
                                    LogID,
                                    pdu.Source,
                                    pdu.Reason);
                                if (this is IDicomServiceProvider) (this as IDicomServiceProvider).OnReceiveAbort(pdu.Source, pdu.Reason);
                                else if (this is IDicomServiceUser) (this as IDicomServiceUser).OnReceiveAbort(pdu.Source, pdu.Reason);
                                CloseConnection(null);
                                return;
                            }
                        case 0xFF:
                            {
                                break;
                            }
                        default:
                            throw new DicomNetworkException("Unknown PDU type");
                    }
                }
            }
            catch (ObjectDisposedException)
            {
                // silently ignore
                CloseConnection(null);
            }
            catch (NullReferenceException)
            {
                // connection already closed; silently ignore
                CloseConnection(null);
            }
            catch (IOException e)
            {
                LogIOException(this.Logger, e, true);
                CloseConnection(e);
            }
            catch (Exception e)
            {
                Logger.Error("Exception processing PDU: {@error}", e);
                CloseConnection(e);
            }
        }

        /// <summary>
        /// Process P-DATA-TF PDUs.
        /// </summary>
        /// <param name="pdu">PDU to process.</param>
        private async Task ProcessPDataTFAsync(PDataTF pdu)
        {
            try
            {
                foreach (var pdv in pdu.PDVs)
                {
                    if (_dimse == null)
                    {
                        // create stream for receiving command
                        if (_dimseStream == null)
                        {
                            _dimseStream = new MemoryStream();
                            _dimseStreamFile = null;
                        }
                    }
                    else
                    {
                        // create stream for receiving dataset
                        if (_dimseStream == null)
                        {
                            if (_dimse.Type == DicomCommandField.CStoreRequest)
                            {
                                var pc = Association.PresentationContexts.FirstOrDefault(x => x.ID == pdv.PCID);

                                var file = new DicomFile();
                                file.FileMetaInfo.MediaStorageSOPClassUID = pc.AbstractSyntax;
                                file.FileMetaInfo.MediaStorageSOPInstanceUID =
                                    _dimse.Command.Get<DicomUID>(DicomTag.AffectedSOPInstanceUID);
                                file.FileMetaInfo.TransferSyntax = pc.AcceptedTransferSyntax;
                                file.FileMetaInfo.ImplementationClassUID = Association.RemoteImplemetationClassUID;
                                file.FileMetaInfo.ImplementationVersionName = Association.RemoteImplementationVersion;
                                file.FileMetaInfo.SourceApplicationEntityTitle = Association.CallingAE;

                                CreateCStoreReceiveStream(file);
                            }
                            else
                            {
                                _dimseStream = new MemoryStream();
                                _dimseStreamFile = null;
                            }
                        }
                    }

                    await this._dimseStream.WriteAsync(pdv.Value, 0, pdv.Value.Length).ConfigureAwait(false);

                    if (pdv.IsLastFragment)
                    {
                        if (pdv.IsCommand)
                        {
                            _dimseStream.Seek(0, SeekOrigin.Begin);

                            var command = new DicomDataset();

                            var reader = new DicomReader();
                            reader.IsExplicitVR = false;
                            reader.Read(new StreamByteSource(_dimseStream), new DicomDatasetReaderObserver(command));

                            _dimseStream = null;
                            _dimseStreamFile = null;

                            var type = command.Get<DicomCommandField>(DicomTag.CommandField);
                            switch (type)
                            {
                                case DicomCommandField.CStoreRequest:
                                    _dimse = new DicomCStoreRequest(command);
                                    break;
                                case DicomCommandField.CStoreResponse:
                                    _dimse = new DicomCStoreResponse(command);
                                    break;
                                case DicomCommandField.CFindRequest:
                                    _dimse = new DicomCFindRequest(command);
                                    break;
                                case DicomCommandField.CFindResponse:
                                    _dimse = new DicomCFindResponse(command);
                                    break;
                                case DicomCommandField.CMoveRequest:
                                    _dimse = new DicomCMoveRequest(command);
                                    break;
                                case DicomCommandField.CMoveResponse:
                                    _dimse = new DicomCMoveResponse(command);
                                    break;
                                case DicomCommandField.CEchoRequest:
                                    _dimse = new DicomCEchoRequest(command);
                                    break;
                                case DicomCommandField.CEchoResponse:
                                    _dimse = new DicomCEchoResponse(command);
                                    break;
                                case DicomCommandField.NActionRequest:
                                    _dimse = new DicomNActionRequest(command);
                                    break;
                                case DicomCommandField.NActionResponse:
                                    _dimse = new DicomNActionResponse(command);
                                    break;
                                case DicomCommandField.NCreateRequest:
                                    _dimse = new DicomNCreateRequest(command);
                                    break;
                                case DicomCommandField.NCreateResponse:
                                    _dimse = new DicomNCreateResponse(command);
                                    break;
                                case DicomCommandField.NDeleteRequest:
                                    _dimse = new DicomNDeleteRequest(command);
                                    break;
                                case DicomCommandField.NDeleteResponse:
                                    _dimse = new DicomNDeleteResponse(command);
                                    break;
                                case DicomCommandField.NEventReportRequest:
                                    _dimse = new DicomNEventReportRequest(command);
                                    break;
                                case DicomCommandField.NEventReportResponse:
                                    _dimse = new DicomNEventReportResponse(command);
                                    break;
                                case DicomCommandField.NGetRequest:
                                    _dimse = new DicomNGetRequest(command);
                                    break;
                                case DicomCommandField.NGetResponse:
                                    _dimse = new DicomNGetResponse(command);
                                    break;
                                case DicomCommandField.NSetRequest:
                                    _dimse = new DicomNSetRequest(command);
                                    break;
                                case DicomCommandField.NSetResponse:
                                    _dimse = new DicomNSetResponse(command);
                                    break;
                                default:
                                    _dimse = new DicomMessage(command);
                                    break;
                            }
                            _dimse.PresentationContext =
                                Association.PresentationContexts.FirstOrDefault(x => x.ID == pdv.PCID);
                            if (!_dimse.HasDataset)
                            {
                                this.PerformDimse(this._dimse);
                                _dimse = null;
                                return;
                            }
                        }
                        else
                        {
                            if (_dimse.Type != DicomCommandField.CStoreRequest)
                            {
                                _dimseStream.Seek(0, SeekOrigin.Begin);

                                var pc = Association.PresentationContexts.FirstOrDefault(x => x.ID == pdv.PCID);

                                _dimse.Dataset = new DicomDataset();
                                _dimse.Dataset.InternalTransferSyntax = pc.AcceptedTransferSyntax;

                                var source = new StreamByteSource(_dimseStream);
                                source.Endian = pc.AcceptedTransferSyntax.Endian;

                                var reader = new DicomReader();
                                reader.IsExplicitVR = pc.AcceptedTransferSyntax.IsExplicitVR;
                                reader.Read(source, new DicomDatasetReaderObserver(_dimse.Dataset));

                                _dimseStream = null;
                                _dimseStreamFile = null;
                            }
                            else
                            {
                                var request = _dimse as DicomCStoreRequest;

                                try
                                {
                                    var dicomFile = GetCStoreDicomFile();
                                    _dimseStream = null;
                                    _dimseStreamFile = null;

                                    // NOTE: dicomFile will be valid with the default implementation of CreateCStoreReceiveStream() and
                                    // GetCStoreDicomFile(), but can be null if a child class overrides either method and changes behavior.
                                    // See documentation on CreateCStoreReceiveStream() and GetCStoreDicomFile() for information about why
                                    // this might be desired.
                                    request.File = dicomFile;
                                    if (request.File != null)
                                    {
                                        request.Dataset = request.File.Dataset;
                                    }
                                }
                                catch (Exception e)
                                {
                                    // failed to parse received DICOM file; send error response instead of aborting connection
                                    SendResponse(
                                        new DicomCStoreResponse(
                                            request,
                                            new DicomStatus(DicomStatus.ProcessingFailure, e.Message)));
                                    Logger.Error("Error parsing C-Store dataset: {@error}", e);
                                    (this as IDicomCStoreProvider).OnCStoreRequestException(
                                        _dimseStreamFile != null ? _dimseStreamFile.Name : null, e);
                                    return;
                                }
                            }

                            this.PerformDimse(this._dimse);
                            _dimse = null;
                        }
                    }
                }
            }
            catch (Exception e)
            {
                SendAbort(DicomAbortSource.ServiceUser, DicomAbortReason.NotSpecified);
                Logger.Error("Exception processing P-Data-TF PDU: {@error}", e);
            }
            finally
            {
                SendNextMessage();
            }
        }

        private void PerformDimse(DicomMessage dimse)
        {
            try
            {
                Logger.Info("{logId} <- {dicomMessage}", LogID, dimse.ToString(Options.LogDimseDatasets));

                if (!DicomMessage.IsRequest(dimse.Type))
                {
                    var rsp = dimse as DicomResponse;
                    lock (_lock)
                    {
                        var req = _pending.FirstOrDefault(x => x.MessageID == rsp.RequestMessageID);
                        if (req != null)
                        {
                            rsp.UserState = req.UserState;
                            req.PostResponse(this, rsp);
                            if (rsp.Status.State != DicomState.Pending) _pending.Remove(req);
                        }
                    }
                    return;
                }

                if (dimse.Type == DicomCommandField.CStoreRequest)
                {
                    if (this is IDicomCStoreProvider)
                    {
                        var response = (this as IDicomCStoreProvider).OnCStoreRequest(dimse as DicomCStoreRequest);
                        SendResponse(response);
                        return;
                    }
                    else throw new DicomNetworkException("C-Store SCP not implemented");
                }

                if (dimse.Type == DicomCommandField.CFindRequest)
                {
                    if (this is IDicomCFindProvider)
                    {
                        var responses = (this as IDicomCFindProvider).OnCFindRequest(dimse as DicomCFindRequest);
                        foreach (var response in responses) SendResponse(response);
                        return;
                    }
                    else throw new DicomNetworkException("C-Find SCP not implemented");
                }

                if (dimse.Type == DicomCommandField.CMoveRequest)
                {
                    if (this is IDicomCMoveProvider)
                    {
                        var responses = (this as IDicomCMoveProvider).OnCMoveRequest(dimse as DicomCMoveRequest);
                        foreach (var response in responses) SendResponse(response);
                        return;
                    }
                    else throw new DicomNetworkException("C-Move SCP not implemented");
                }

                if (dimse.Type == DicomCommandField.CEchoRequest)
                {
                    if (this is IDicomCEchoProvider)
                    {
                        var response = (this as IDicomCEchoProvider).OnCEchoRequest(dimse as DicomCEchoRequest);
                        SendResponse(response);
                        return;
                    }
                    else throw new DicomNetworkException("C-Echo SCP not implemented");
                }

                if (dimse.Type == DicomCommandField.NActionRequest || dimse.Type == DicomCommandField.NCreateRequest
                    || dimse.Type == DicomCommandField.NDeleteRequest
                    || dimse.Type == DicomCommandField.NEventReportRequest
                    || dimse.Type == DicomCommandField.NGetRequest || dimse.Type == DicomCommandField.NSetRequest)
                {
                    if (!(this is IDicomNServiceProvider)) throw new DicomNetworkException("N-Service SCP not implemented");

                    DicomResponse response = null;
                    if (dimse.Type == DicomCommandField.NActionRequest) response = (this as IDicomNServiceProvider).OnNActionRequest(dimse as DicomNActionRequest);
                    else if (dimse.Type == DicomCommandField.NCreateRequest) response = (this as IDicomNServiceProvider).OnNCreateRequest(dimse as DicomNCreateRequest);
                    else if (dimse.Type == DicomCommandField.NDeleteRequest)
                        response =
                            (this as IDicomNServiceProvider).OnNDeleteRequest(dimse as DicomNDeleteRequest);
                    else if (dimse.Type == DicomCommandField.NEventReportRequest)
                        response =
                            (this as IDicomNServiceProvider).OnNEventReportRequest(
                                dimse as DicomNEventReportRequest);
                    else if (dimse.Type == DicomCommandField.NGetRequest)
                        response =
                            (this as IDicomNServiceProvider).OnNGetRequest(dimse as DicomNGetRequest);
                    else if (dimse.Type == DicomCommandField.NSetRequest)
                        response =
                            (this as IDicomNServiceProvider).OnNSetRequest(
                                dimse as DicomNSetRequest);

                    SendResponse(response);
                    return;
                }

                throw new DicomNetworkException("Operation not implemented");
            }
            finally
            {
                SendNextMessage();
            }
        }

        private async Task SendNextPDUAsync()
        {
            while (true)
            {
                if (!_isConnected) return;

                PDU pdu;

                lock (_lock)
                {
                    if (_writing) return;

                    if (_pduQueue.Count == 0) return;

                    _writing = true;

                    pdu = _pduQueue.Dequeue();
                }

                if (Options.LogDataPDUs && pdu is PDataTF) Logger.Info("{logId} -> {pdu}", LogID, pdu);

                MemoryStream ms = new MemoryStream();
                pdu.Write().WritePDU(ms);

                byte[] buffer = ms.ToArray();

                try
                {
                    await this._network.WriteAsync(buffer, 0, (int)ms.Length).ConfigureAwait(false);
                }
                catch (IOException e)
                {
                    LogIOException(this.Logger, e, false);
                    CloseConnection(e);
                }
                catch
                {
                }

                lock (_lock) _writing = false;
            }
        }

        private void SendMessage(DicomMessage message)
        {
            lock (_lock) _msgQueue.Enqueue(message);
            SendNextMessage();
        }

        private void SendNextMessage()
        {
            while (true)
            {
                DicomMessage msg;

                lock (_lock)
                {
                    if (_msgQueue.Count == 0)
                    {
                        if (_pending.Count == 0) OnSendQueueEmpty();
                        return;
                    }

                    if (_sending) return;

                    if (Association.MaxAsyncOpsInvoked > 0 && _pending.Count >= Association.MaxAsyncOpsInvoked) return;

                    _sending = true;

                    msg = _msgQueue.Dequeue();
                }

                if (msg is DicomRequest) _pending.Add(msg as DicomRequest);

                DicomPresentationContext pc = null;
                if (msg is DicomCStoreRequest)
                {
                    pc =
                        Association.PresentationContexts.FirstOrDefault(
                            x =>
                                x.Result == DicomPresentationContextResult.Accept && x.AbstractSyntax == msg.SOPClassUID
                                && x.AcceptedTransferSyntax == (msg as DicomCStoreRequest).TransferSyntax);
                    if (pc == null)
                        pc =
                            Association.PresentationContexts.FirstOrDefault(
                                x =>
                                    x.Result == DicomPresentationContextResult.Accept &&
                                    x.AbstractSyntax == msg.SOPClassUID);
                }
                else
                {
                    pc =
                        Association.PresentationContexts.FirstOrDefault(
                            x =>
                                x.Result == DicomPresentationContextResult.Accept && x.AbstractSyntax == msg.SOPClassUID);
                }

                if (pc == null)
                {
                    pc = msg.PresentationContext;
                }

                if (pc == null)
                {
                    _pending.Remove(msg as DicomRequest);

                    try
                    {
                        if (msg is DicomCStoreRequest)
                            (msg as DicomCStoreRequest).PostResponse(
                                this,
                                new DicomCStoreResponse(msg as DicomCStoreRequest, DicomStatus.SOPClassNotSupported));
                        else if (msg is DicomCEchoRequest)
                            (msg as DicomCEchoRequest).PostResponse(
                                this,
                                new DicomCEchoResponse(msg as DicomCEchoRequest, DicomStatus.SOPClassNotSupported));
                        else if (msg is DicomCFindRequest)
                            (msg as DicomCFindRequest).PostResponse(
                                this,
                                new DicomCFindResponse(msg as DicomCFindRequest, DicomStatus.SOPClassNotSupported));
                        else if (msg is DicomCMoveRequest)
                            (msg as DicomCMoveRequest).PostResponse(
                                this,
                                new DicomCMoveResponse(
                                    msg as DicomCMoveRequest,
                                    DicomStatus.SOPClassNotSupported));

                        //TODO: add N services
                    }
                    catch
                    {
                    }

                    Logger.Error(
                        "No accepted presentation context found for abstract syntax: {sopClassUid}",
                        msg.SOPClassUID);
                }
                else
                {
                    var dimse = new Dimse
                    {
                        Message = msg,
                        PresentationContext = pc
                    };

                    // force calculation of command group length as required by standard
                    msg.Command.RecalculateGroupLengths();

                    if (msg.HasDataset)
                    {
                        // remove group lengths as recommended in PS 3.5 7.2
                        //
                        //	2. It is recommended that Group Length elements be removed during storage or transfer 
                        //	   in order to avoid the risk of inconsistencies arising during coercion of data 
                        //	   element values and changes in transfer syntax.
                        msg.Dataset.RemoveGroupLengths();

                        if (msg.Dataset.InternalTransferSyntax != dimse.PresentationContext.AcceptedTransferSyntax)
                            msg.Dataset =
                                msg.Dataset.ChangeTransferSyntax(dimse.PresentationContext.AcceptedTransferSyntax);
                    }

                    Logger.Info("{logId} -> {dicomMessage}", LogID, msg.ToString(Options.LogDimseDatasets));

                    try
                    {
                        dimse.Stream = new PDataTFStream(this, pc.ID, Association.MaximumPDULength);

                        var writer = new DicomWriter(
                            DicomTransferSyntax.ImplicitVRLittleEndian,
                            DicomWriteOptions.Default,
                            new StreamByteTarget(dimse.Stream));

                        dimse.Walker = new DicomDatasetWalker(msg.Command);
                        dimse.Walker.Walk(writer);

                        if (dimse.Message.HasDataset)
                        {
                            dimse.Stream.IsCommand = false;

                            writer = new DicomWriter(
                                dimse.PresentationContext.AcceptedTransferSyntax,
                                DicomWriteOptions.Default,
                                new StreamByteTarget(dimse.Stream));

                            dimse.Walker = new DicomDatasetWalker(dimse.Message.Dataset);
                            dimse.Walker.Walk(writer);
                        }
                    }
                    catch (Exception e)
                    {
                        Logger.Error("Exception sending DIMSE: {@error}", e);
                    }
                    finally
                    {
                        dimse.Stream.Flush(true).Wait();
                        dimse.Stream.Dispose();
                    }
                }

                lock (_lock) _sending = false;
            }
        }

        #endregion

        #region Send Methods

        /// <summary>
        /// Send association request.
        /// </summary>
        /// <param name="association">DICOM association.</param>
        protected void SendAssociationRequest(DicomAssociation association)
        {
            LogID = association.CalledAE;
            if (Options.UseRemoteAEForLogName) Logger = LogManager.GetLogger(LogID);
            Logger.Info("{calledAE} -> Association request:\n{association}", LogID, association.ToString());
            Association = association;
            this.SendPDUAsync(new AAssociateRQ(Association)).Wait();
        }

        /// <summary>
        /// Send association accept response.
        /// </summary>
        /// <param name="association">DICOM association.</param>
        protected void SendAssociationAccept(DicomAssociation association)
        {
            Association = association;

            // reject all presentation contexts that have not already been accepted or rejected
            foreach (var pc in Association.PresentationContexts)
            {
                if (pc.Result == DicomPresentationContextResult.Proposed) pc.SetResult(DicomPresentationContextResult.RejectNoReason);
            }

            Logger.Info("{logId} -> Association accept:\n{association}", LogID, association.ToString());
            this.SendPDUAsync(new AAssociateAC(Association)).Wait();
        }

        /// <summary>
        /// Send association reject response.
        /// </summary>
        /// <param name="result">Rejection result.</param>
        /// <param name="source">Rejection source.</param>
        /// <param name="reason">Rejection reason.</param>
        protected void SendAssociationReject(
            DicomRejectResult result,
            DicomRejectSource source,
            DicomRejectReason reason)
        {
            Logger.Info(
                "{logId} -> Association reject [result: {result}; source: {source}; reason: {reason}]",
                LogID,
                result,
                source,
                reason);
            this.SendPDUAsync(new AAssociateRJ(result, source, reason)).Wait();
        }

        /// <summary>
        /// Send association release request.
        /// </summary>
        protected void SendAssociationReleaseRequest()
        {
            Logger.Info("{logId} -> Association release request", LogID);
            this.SendPDUAsync(new AReleaseRQ()).Wait();
        }

        /// <summary>
        /// Send association release response.
        /// </summary>
        protected void SendAssociationReleaseResponse()
        {
            Logger.Info("{logId} -> Association release response", LogID);
            this.SendPDUAsync(new AReleaseRP()).Wait();
        }

        /// <summary>
        /// Send abort request.
        /// </summary>
        /// <param name="source"></param>
        /// <param name="reason"></param>
        protected void SendAbort(DicomAbortSource source, DicomAbortReason reason)
        {
            Logger.Info("{logId} -> Abort [source: {source}; reason: {reason}]", LogID, source, reason);
            this.SendPDUAsync(new AAbort(source, reason)).Wait();
        }

        #endregion

        #region Override Methods

        /// <summary>
        /// Action to perform when send queue is empty.
        /// </summary>
        protected virtual void OnSendQueueEmpty()
        {
        }

        #endregion

        #region Helper methods

        private static void LogIOException(Logger logger, Exception e, bool reading)
        {
            var socketFmt = string.Format(@"Socket error {0} PDU: {{socketErrorCode}} [{{errorCode}}]", reading ? "reading" : "writing");
            var otherFmt = string.Format(@"IO exception while {0} PDU: {{@error}}", reading ? "reading" : "writing");

            int errorCode;
            string errorDescriptor;
            if (NetworkManager.IsSocketException(e, out errorCode, out errorDescriptor))
            {
                logger.Error(socketFmt, errorDescriptor, errorCode);
            }
            else if (!(e.InnerException is ObjectDisposedException))
            {
                logger.Error(otherFmt, e);
            }
        }

        #endregion

        #region INNER TYPES

        private class Dimse
        {
            public DicomMessage Message;

            public PDataTFStream Stream;

            public DicomDatasetWalker Walker;

            public DicomPresentationContext PresentationContext;
        }

        private class PDataTFStream : Stream
        {
            #region Private Members

            private readonly DicomService _service;

            private bool _command;

            private readonly uint _pduMax;

            private uint _max;

            private readonly byte _pcid;

            private PDataTF _pdu;

            private byte[] _bytes;

            private int _length;

            #endregion

            #region Public Constructors

            public PDataTFStream(DicomService service, byte pcid, uint max)
            {
                _service = service;
                _command = true;
                _pcid = pcid;
                _pduMax = Math.Min(max, Int32.MaxValue);
                _max = (_pduMax == 0)
                           ? _service.Options.MaxCommandBuffer
                           : Math.Min(_pduMax, _service.Options.MaxCommandBuffer);

                _pdu = new PDataTF();

                // Max PDU Size - Current Size - Size of PDV header
                _bytes = new byte[_max - CurrentPduSize() - 6];
            }

            #endregion

            #region Public Properties

            public bool IsCommand
            {
                get
                {
                    return _command;
                }
                set
                {
                    // recalculate maximum PDU buffer size
                    if (_command != value)
                    {
                        if (value)
                            _max = (_pduMax == 0)
                                       ? _service.Options.MaxCommandBuffer
                                       : Math.Min(_pduMax, _service.Options.MaxCommandBuffer);
                        else
                            _max = (_pduMax == 0)
                                       ? _service.Options.MaxDataBuffer
                                       : Math.Min(_pduMax, _service.Options.MaxDataBuffer);

                        CreatePDV(true).Wait();
                        _command = value;
                    }
                }
            }

            #endregion

            #region Public Members

            public async Task Flush(bool last)
            {
                await CreatePDV(last).ConfigureAwait(false);
                await this.WritePDUAsync(last).ConfigureAwait(false);
            }

            #endregion

            #region Private Members

            private uint CurrentPduSize()
            {
                // PDU header + PDV header + PDV data
                return 6 + _pdu.GetLengthOfPDVs();
            }

            private async Task CreatePDV(bool last)
            {
                try
                {
                    if (_bytes == null) _bytes = new byte[0];

                    if (_length < _bytes.Length) Array.Resize(ref _bytes, _length);

                    PDV pdv = new PDV(_pcid, _bytes, _command, last);
                    _pdu.PDVs.Add(pdv);

                    //_service.Logger.Info(pdv);

                    // reset length in case we recurse into WritePDU()
                    _length = 0;
                    // is the current PDU at its maximum size or do we have room for another PDV?
                    if ((CurrentPduSize() + 6) >= _max || (!_command && last)) await this.WritePDUAsync(last).ConfigureAwait(false);

                    // Max PDU Size - Current Size - Size of PDV header
                    uint max = _max - CurrentPduSize() - 6;
                    _bytes = last ? null : new byte[max];
                }
                catch (Exception e)
                {
                    _service.Logger.Error("Exception creating PDV: {@error}", e);
                    throw;
                }
            }

            private async Task WritePDUAsync(bool last)
            {
                if (_length > 0) await CreatePDV(last).ConfigureAwait(false);

                if (_pdu.PDVs.Count > 0)
                {
                    if (last) _pdu.PDVs[_pdu.PDVs.Count - 1].IsLastFragment = true;

                    await _service.SendPDUAsync(_pdu).ConfigureAwait(false);

                    _pdu = new PDataTF();
                }
            }

            #endregion

            #region Stream Members

            public override bool CanRead
            {
                get
                {
                    return false;
                }
            }

            public override bool CanSeek
            {
                get
                {
                    return false;
                }
            }

            public override bool CanWrite
            {
                get
                {
                    return true;
                }
            }

            public override void Flush()
            {
            }

            public override long Length
            {
                get
                {
                    throw new NotImplementedException();
                }
            }

            public override long Position
            {
                get
                {
                    throw new NotImplementedException();
                }
                set
                {
                    throw new NotImplementedException();
                }
            }

            public override int Read(byte[] buffer, int offset, int count)
            {
                throw new NotImplementedException();
            }

            public override long Seek(long offset, SeekOrigin origin)
            {
                throw new NotImplementedException();
            }

            public override void SetLength(long value)
            {
                throw new NotImplementedException();
            }

            public override void Write(byte[] buffer, int offset, int count)
            {
                try
                {
                    if (_bytes == null || _bytes.Length == 0)
                    {
                        // Max PDU Size - Current Size - Size of PDV header
                        uint max = _max - CurrentPduSize() - 6;
                        _bytes = new byte[max];
                    }

                    while (count >= (_bytes.Length - _length))
                    {
                        int c = Math.Min(count, _bytes.Length - _length);

                        Array.Copy(buffer, offset, _bytes, _length, c);

                        _length += c;
                        offset += c;
                        count -= c;

                        CreatePDV(false).Wait();
                    }

                    if (count > 0)
                    {
                        Array.Copy(buffer, offset, _bytes, _length, count);
                        _length += count;

                        if (_bytes.Length == _length) CreatePDV(false).Wait();
                    }
                }
                catch (Exception e)
                {
                    _service.Logger.Error("Exception writing data to PDV: {@error}", e);
                    throw;
                }
            }

            #endregion
        }

        #endregion
    }
}
