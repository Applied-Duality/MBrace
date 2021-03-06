﻿namespace Nessos.MBrace.Utils

    open System
    open System.IO
    open System.Net

    module FixedStream = 

        exception FixedSizeExceededException of unit

        /// A stream wrapper that fails when the size of the stream gt maxSize.
        /// 'Write'-only, no data actually written.
        type FixedSizeStream (maxSize : int64) =
            inherit Stream() with
                let mutable length = 0L
                member private __.LengthInternal
                    with get () = length
                    and  set l = 
                        if l > maxSize then raise(FixedSizeExceededException())
                        else length <- l
                let mutable position = 0L

                override this.CanRead  = false
                override this.CanSeek  = false
                override this.CanWrite = true
                override this.Length = this.LengthInternal
                override this.Position 
                    with get () = position
                    and  set v = position <- v
                override this.Flush () = ()
                override this.Seek(_,_) = raise(NotSupportedException())
                override this.SetLength l = this.LengthInternal <- l
                override this.Read(_,_,_) = raise(NotSupportedException())
                override this.Write(bytes, offset, count) =
                    let len = bytes.Length
                    if offset >= len || offset + count > len || offset < 0 then
                        raise(ArgumentException("Offset and length were out of bounds for the array or count is greater than the number of elements from index to the end of the source collection."))
                    else 
                        position <- position + int64 count
                        if position > this.LengthInternal then
                            this.LengthInternal <- position


    module Substream = 

        /// A stream wrapper that sets as position 0L a given offset of the
        /// underlying stream.
        /// Please find a better name for this one.
        type Substream (underlying : Stream, offset : int) =
            inherit Stream() with
                override this.CanRead  = underlying.CanRead
                override this.CanSeek  = underlying.CanSeek
                override this.CanWrite = underlying.CanWrite
                
                override this.Length = underlying.Length - int64 offset
                
                override this.Position
                    with get () = underlying.Position - int64 offset
                    and  set p  = underlying.Position <- p

                override this.Flush () = underlying.Flush()
                
                override this.Seek(offset', origin) = 
                    match origin with
                    | SeekOrigin.Begin -> underlying.Seek(offset' + int64 offset, SeekOrigin.Begin)
                    | _ -> underlying.Seek(offset', origin)

                override this.SetLength l = underlying.SetLength(int64 offset + l)

                override this.Read(buffer,offset',count) = 
                    underlying.Read(buffer, offset' + offset, count)
                override this.Write(bytes, offset' , count) =
                    underlying.Write(bytes, offset' + offset, count)


        type StreamWrapper(stream : Stream, low : int64) =
            inherit Stream() with
                do stream.Position <- low

                override this.CanRead = stream.CanRead
                override this.CanSeek = stream.CanSeek
                override this.CanWrite = stream.CanWrite
                override this.Length = stream.Length - low
                override this.Position 
                    with get () = stream.Position - low
                    and  set p  = stream.Position <- p + low
                override this.Flush () = stream.Flush()
                override this.Seek(offset : int64, origin : SeekOrigin) =
                    match origin with
                    | SeekOrigin.Begin -> stream.Seek(offset + low, SeekOrigin.Begin) - low
                    | SeekOrigin.End -> stream.Seek(offset, SeekOrigin.End) - low
                    | SeekOrigin.Current 
                    | _ as origin -> stream.Seek(offset, SeekOrigin.Current) - low
                override this.SetLength(l : int64) = stream.SetLength(l + low)
                override this.Read(buffer : byte [], offset : int, count : int) =
                    stream.Read(buffer, offset, count)
                override this.Write(buffer : byte [], offset : int, count : int) =
                    stream.Write(buffer, offset, count)