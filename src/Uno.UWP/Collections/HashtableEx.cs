﻿#pragma warning disable CS8981

#nullable enable
// Based on https://github.com/dotnet/runtime/commit/bdc8e420aa75999021e06b85e2e1869962730a0f

// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Collections;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Threading;

namespace Uno.Collections
{
	/// <summary>
	/// Specialized version of <see cref="Hashtable"/> providing TryGetValue and single-threaded optimizations
	/// </summary>
	[DebuggerDisplay("Count = {Count}")]
	internal class HashtableEx : IEnumerable
	{
		/*
          This Hashtable uses double hashing.  There are hashsize buckets in the
          table, and each bucket can contain 0 or 1 element.  We use a bit to mark
          whether there's been a collision when we inserted multiple elements
          (ie, an inserted item was hashed at least a second time and we probed
          this bucket, but it was already in use).  Using the collision bit, we
          can terminate lookups & removes for elements that aren't in the hash
          table more quickly.  We steal the most significant bit from the hash code
          to store the collision bit.

          Our hash function is of the following form:

          h(key, n) = h1(key) + n*h2(key)

          where n is the number of times we've hit a collided bucket and rehashed
          (on this particular lookup).  Here are our hash functions:

          h1(key) = GetHash(key);  // default implementation calls key.GetHashCode();
          h2(key) = 1 + (((h1(key) >> 5) + 1) % (hashsize - 1));

          The h1 can return any number.  h2 must return a number between 1 and
          hashsize - 1 that is relatively prime to hashsize (not a problem if
          hashsize is prime).  (Knuth's Art of Computer Programming, Vol. 3, p. 528-9)
          If this is true, then we are guaranteed to visit every bucket in exactly
          hashsize probes, since the least common multiple of hashsize and h2(key)
          will be hashsize * h2(key).  (This is the first number where adding h2 to
          h1 mod hashsize will be 0 and we will search the same bucket twice).

          We previously used a different h2(key, n) that was not constant.  That is a
          horrifically bad idea, unless you can prove that series will never produce
          any identical numbers that overlap when you mod them by hashsize, for all
          subranges from i to i+hashsize, for all i.  It's not worth investigating,
          since there was no clear benefit from using that hash function, and it was
          broken.

          For efficiency reasons, we've implemented this by storing h1 and h2 in a
          temporary, and setting a variable called seed equal to h1.  We do a probe,
          and if we collided, we simply add h2 to seed each time through the loop.

          A good test for h2() is to subclass Hashtable, provide your own implementation
          of GetHash() that returns a constant, then add many items to the hash table.
          Make sure Count equals the number of items you inserted.

          Note that when we remove an item from the hash table, we set the key
          equal to buckets, if there was a collision in this bucket.  Otherwise
          we'd either wipe out the collision bit, or we'd still have an item in
          the hash table.

           --
        */

		private const int InitialSize = 3;

		// Deleted entries have their key set to buckets

		// The hash table data.
		// This cannot be serialized
		private struct bucket
		{
			public object? key;
			public object? val;
			public int hash_coll;   // Store hash code; sign bit means there was a collision.
		}

		private readonly struct buckets
		{
			public readonly int Length;

			public readonly bucket[] Array;

			public buckets(int length)
			{
				Length = length;
				Array = Uno.Buffers.ArrayPool<bucket>.Shared.Rent(length);
			}

			public void Return()
			{
				Uno.Buffers.ArrayPool<bucket>.Shared.Return(Array, true);
			}
		}

		private buckets _buckets;

		// The total number of entries in the hash table.
		private int _count;

		// The total number of collision bits set in the hashtable
		private int _occupancy;

		private int _loadsize;
		private float _loadFactor;

		private ICollection? _keys;
		private ICollection? _values;

		private IEqualityComparer? _keycomparer;

		protected IEqualityComparer? EqualityComparer => _keycomparer;

		// Constructs a new hashtable. The hashtable is created with an initial
		// capacity of zero and a load factor of 1.0.
		public HashtableEx() : this(0, 1.0f)
		{
		}

		// Constructs a new hashtable with the given initial capacity and a load
		// factor of 1.0. The capacity argument serves as an indication of
		// the number of entries the hashtable will contain. When this number (or
		// an approximation) is known, specifying it in the constructor can
		// eliminate a number of resizing operations that would otherwise be
		// performed when elements are added to the hashtable.
		//
		public HashtableEx(int capacity) : this(capacity, 1.0f)
		{
		}

		// Constructs a new hashtable with the given initial capacity and load
		// factor. The capacity argument serves as an indication of the
		// number of entries the hashtable will contain. When this number (or an
		// approximation) is known, specifying it in the constructor can eliminate
		// a number of resizing operations that would otherwise be performed when
		// elements are added to the hashtable. The loadFactor argument
		// indicates the maximum ratio of hashtable entries to hashtable buckets.
		// Smaller load factors cause faster average lookup times at the cost of
		// increased memory consumption. A load factor of 1.0 generally provides
		// the best balance between speed and size.
		//
		public HashtableEx(int capacity, float loadFactor)
		{
			if (capacity < 0)
				throw new ArgumentOutOfRangeException(nameof(capacity), "SR.ArgumentOutOfRange_NeedNonNegNum");
			if (!(loadFactor >= 0.1f && loadFactor <= 1.0f))
				throw new ArgumentOutOfRangeException(nameof(loadFactor) /*, SR.Format("SR.ArgumentOutOfRange_HashtableLoadFactor", .1, 1.0)*/);

			// Based on perf work, .72 is the optimal load factor for this table.
			_loadFactor = 0.72f * loadFactor;

			double rawsize = capacity / _loadFactor;
			if (rawsize > int.MaxValue)
				throw new ArgumentException("SR.Arg_HTCapacityOverflow", nameof(capacity));

			// Avoid awfully small sizes
			int hashsize = (rawsize > InitialSize) ? HashHelpers.GetPrime((int)rawsize) : InitialSize;
			_buckets = new buckets(hashsize);

			_loadsize = (int)(_loadFactor * hashsize);
			// Based on the current algorithm, loadsize must be less than hashsize.
			Debug.Assert(_loadsize < hashsize, "Invalid hashtable loadsize!");
		}

		public HashtableEx(int capacity, float loadFactor, IEqualityComparer? equalityComparer) : this(capacity, loadFactor)
		{
			_keycomparer = equalityComparer;
		}

		public HashtableEx(IEqualityComparer? equalityComparer) : this(0, 1.0f, equalityComparer)
		{
		}

		public HashtableEx(int capacity, IEqualityComparer? equalityComparer)
			: this(capacity, 1.0f, equalityComparer)
		{
		}

		// Constructs a new hashtable containing a copy of the entries in the given
		// dictionary. The hashtable is created with a load factor of 1.0.
		//
		public HashtableEx(HashtableEx d) : this(d, 1.0f)
		{
		}

		// Constructs a new hashtable containing a copy of the entries in the given
		// dictionary. The hashtable is created with the given load factor.
		//
		public HashtableEx(HashtableEx d, float loadFactor)
			: this(d, loadFactor, (IEqualityComparer?)null)
		{
		}

		public HashtableEx(HashtableEx d, IEqualityComparer? equalityComparer)
			: this(d, 1.0f, equalityComparer)
		{
		}

		public HashtableEx(HashtableEx d, float loadFactor, IEqualityComparer? equalityComparer)
			: this(d != null ? d.Count : 0, loadFactor, equalityComparer)
		{
			if (d == null)
				throw new ArgumentNullException(nameof(d), "SR.ArgumentNull_Dictionary");

			IDictionaryEnumerator e = d.GetEnumerator();
			while (e.MoveNext())
				Add(e.Key, e.Value);
		}

		public void Dispose()
		{
			_buckets.Return();
		}

		// ?InitHash? is basically an implementation of classic DoubleHashing (see http://en.wikipedia.org/wiki/Double_hashing)
		//
		// 1) The only ?correctness? requirement is that the ?increment? used to probe
		//    a. Be non-zero
		//    b. Be relatively prime to the table size ?hashSize?. (This is needed to insure you probe all entries in the table before you ?wrap? and visit entries already probed)
		// 2) Because we choose table sizes to be primes, we just need to insure that the increment is 0 < incr < hashSize
		//
		// Thus this function would work: Incr = 1 + (seed % (hashSize-1))
		//
		// While this works well for ?uniformly distributed? keys, in practice, non-uniformity is common.
		// In particular in practice we can see ?mostly sequential? where you get long clusters of keys that ?pack?.
		// To avoid bad behavior you want it to be the case that the increment is ?large? even for ?small? values (because small
		// values tend to happen more in practice). Thus we multiply ?seed? by a number that will make these small values
		// bigger (and not hurt large values). We picked HashPrime (101) because it was prime, and if ?hashSize-1? is not a multiple of HashPrime
		// (enforced in GetPrime), then incr has the potential of being every value from 1 to hashSize-1. The choice was largely arbitrary.
		//
		// Computes the hash function:  H(key, i) = h1(key) + i*h2(key, hashSize).
		// The out parameter seed is h1(key), while the out parameter
		// incr is h2(key, hashSize).  Callers of this function should
		// add incr each time through a loop.
		private uint InitHash(object key, int hashsize, out uint seed, out uint incr)
		{
			// Hashcode must be positive.  Also, we must not use the sign bit, since
			// that is used for the collision bit.
			uint hashcode = (uint)GetHash(key) & 0x7FFFFFFF;
			seed = (uint)hashcode;
			// Restriction: incr MUST be between 1 and hashsize - 1, inclusive for
			// the modular arithmetic to work correctly.  This guarantees you'll
			// visit every bucket in the table exactly once within hashsize
			// iterations.  Violate this and it'll cause obscure bugs forever.
			// If you change this calculation for h2(key), update putEntry too!
			incr = (uint)(1 + ((seed * HashHelpers.HashPrime) % ((uint)hashsize - 1)));
			return hashcode;
		}

		// Adds an entry with the given key and value to this hashtable. An
		// ArgumentException is thrown if the key is null or if the key is already
		// present in the hashtable.
		//
		public virtual void Add(object key, object? value)
		{
			Insert(key, value, true);
		}

		// Removes all entries from this hashtable.
		public virtual void Clear()
		{
			if (_count == 0 && _occupancy == 0)
				return;

			// Local alias for performance
			var bucketsArray = _buckets.Array;

			for (int i = 0; i < _buckets.Length; i++)
			{
				bucketsArray[i].hash_coll = 0;
				bucketsArray[i].key = null;
				bucketsArray[i].val = null;
			}

			_count = 0;
			_occupancy = 0;
		}

		// Clone returns a virtually identical copy of this hash table.  This does
		// a shallow copy - the Objects in the table aren't cloned, only the references
		// to those Objects.
		public virtual object Clone()
		{
			buckets lbuckets = _buckets;
			HashtableEx ht = new HashtableEx(_count, _keycomparer);
			ht._loadFactor = _loadFactor;
			ht._count = 0;

			int bucket = lbuckets.Length;
			while (bucket > 0)
			{
				bucket--;
				object? keyv = lbuckets.Array[bucket].key;
				if ((keyv != null) && (keyv != lbuckets.Array))
				{
					ht[keyv] = lbuckets.Array[bucket].val;
				}
			}

			return ht;
		}

		// Checks if this hashtable contains the given key.
		public virtual bool Contains(object key)
		{
			return ContainsKey(key);
		}

		// Checks if this hashtable contains an entry with the given key.  This is
		// an O(1) operation.
		//
		public virtual bool ContainsKey(object key)
		{
			if (key == null)
			{
				throw new ArgumentNullException(nameof(key), "SR.ArgumentNull_Key");
			}

			// Take a snapshot of buckets, in case another thread resizes table
			buckets lbuckets = _buckets;
			uint hashcode = InitHash(key, lbuckets.Length, out uint seed, out uint incr);
			int ntry = 0;

			bucket b;
			int bucketNumber = (int)(seed % (uint)lbuckets.Length);
			do
			{
				b = lbuckets.Array[bucketNumber];
				if (b.key == null)
				{
					return false;
				}
				if (((b.hash_coll & 0x7FFFFFFF) == hashcode) &&
					KeyEquals(b.key, key))
					return true;
				bucketNumber = (int)(((long)bucketNumber + incr) % (uint)lbuckets.Length);
			} while (b.hash_coll < 0 && ++ntry < lbuckets.Length);
			return false;
		}

		// Checks if this hashtable contains an entry with the given value. The
		// values of the entries of the hashtable are compared to the given value
		// using the Object.Equals method. This method performs a linear
		// search and is thus be substantially slower than the ContainsKey
		// method.
		//
		public virtual bool ContainsValue(object? value)
		{
			// Local alias for performance
			var bucketsArray = _buckets.Array;

			if (value == null)
			{
				for (int i = _buckets.Length; --i >= 0;)
				{
					if (bucketsArray[i].key != null && bucketsArray[i].key != bucketsArray && bucketsArray[i].val == null)
						return true;
				}
			}
			else
			{
				for (int i = _buckets.Length; --i >= 0;)
				{
					object? val = bucketsArray[i].val;
					if (val != null && val.Equals(value))
						return true;
				}
			}
			return false;
		}

		// Copies the keys of this hashtable to a given array starting at a given
		// index. This method is used by the implementation of the CopyTo method in
		// the KeyCollection class.
		private void CopyKeys(Array array, int arrayIndex)
		{
			Debug.Assert(array != null);
			Debug.Assert(array!.Rank == 1);

			buckets lbuckets = _buckets;
			for (int i = lbuckets.Length; --i >= 0;)
			{
				object? keyv = lbuckets.Array[i].key;
				if ((keyv != null) && (keyv != _buckets.Array))
				{
					array.SetValue(keyv, arrayIndex++);
				}
			}
		}

		// Copies the keys of this hashtable to a given array starting at a given
		// index. This method is used by the implementation of the CopyTo method in
		// the KeyCollection class.
		private void CopyEntries(Array array, int arrayIndex)
		{
			Debug.Assert(array != null);
			Debug.Assert(array!.Rank == 1);

			buckets lbuckets = _buckets;
			for (int i = lbuckets.Length; --i >= 0;)
			{
				object? keyv = lbuckets.Array[i].key;
				if ((keyv != null) && (keyv != _buckets.Array))
				{
					DictionaryEntry entry = new DictionaryEntry(keyv, lbuckets.Array[i].val);
					array.SetValue(entry, arrayIndex++);
				}
			}
		}

		// Copies the values in this hash table to an array at
		// a given index.  Note that this only copies values, and not keys.
		public virtual void CopyTo(Array array, int arrayIndex)
		{
			if (array == null)
				throw new ArgumentNullException(nameof(array), "SR.ArgumentNull_Array");
			if (array.Rank != 1)
				throw new ArgumentException("SR.Arg_RankMultiDimNotSupported", nameof(array));
			if (arrayIndex < 0)
				throw new ArgumentOutOfRangeException(nameof(arrayIndex), "SR.ArgumentOutOfRange_NeedNonNegNum");
			if (array.Length - arrayIndex < Count)
				throw new ArgumentException("SR.Arg_ArrayPlusOffTooSmall");

			CopyEntries(array, arrayIndex);
		}

		// Copies the values in this Hashtable to an KeyValuePairs array.
		// KeyValuePairs is different from Dictionary Entry in that it has special
		// debugger attributes on its fields.

		internal virtual KeyValuePairs[] ToKeyValuePairsArray()
		{
			KeyValuePairs[] array = new KeyValuePairs[_count];
			int index = 0;
			buckets lbuckets = _buckets;
			for (int i = lbuckets.Length; --i >= 0;)
			{
				object? keyv = lbuckets.Array[i].key;
				if ((keyv != null) && (keyv != _buckets.Array))
				{
					array[index++] = new KeyValuePairs(keyv, lbuckets.Array[i].val);
				}
			}

			return array;
		}

		// Copies the values of this hashtable to a given array starting at a given
		// index. This method is used by the implementation of the CopyTo method in
		// the ValueCollection class.
		private void CopyValues(Array array, int arrayIndex)
		{
			Debug.Assert(array != null);
			Debug.Assert(array!.Rank == 1);

			buckets lbuckets = _buckets;
			for (int i = lbuckets.Length; --i >= 0;)
			{
				object? keyv = lbuckets.Array[i].key;
				if ((keyv != null) && (keyv != _buckets.Array))
				{
					array.SetValue(lbuckets.Array[i].val, arrayIndex++);
				}
			}
		}

		// Returns the value associated with the given key. If an entry with the
		// given key is not found, the returned value is null.
		//
		public object? this[object key]
		{
			get
			{
				TryGetValue(key, out var result);
				return result;
			}
			set => Insert(key, value, false);
		}

		public bool TryGetValue(object key, out object? value)
		{
			if (key == null)
			{
				throw new ArgumentNullException(nameof(key), "SR.ArgumentNull_Key");
			}

			// Take a snapshot of buckets, in case another thread does a resize
			buckets lbuckets = _buckets;
			uint hashcode = InitHash(key, lbuckets.Length, out uint seed, out uint incr);
			int ntry = 0;

			bucket b;
			int bucketNumber = (int)(seed % (uint)lbuckets.Length);
			do
			{
				// A read operation on hashtable has three steps:
				//        (1) calculate the hash and find the slot number.
				//        (2) compare the hashcode, if equal, go to step 3. Otherwise end.
				//        (3) compare the key, if equal, go to step 4. Otherwise end.
				//        (4) return the value contained in the bucket.
				//     After step 3 and before step 4. A writer can kick in a remove the old item and add a new one
				//     in the same bucket. So in the reader we need to check if the hash table is modified during above steps.
				//
				// Writers (Insert, Remove, Clear) will set 'isWriterInProgress' flag before it starts modifying
				// the hashtable and will clear the flag when it is done.  When the flag is cleared, the 'version'
				// will be increased.  We will repeat the reading if a writer is in progress or done with the modification
				// during the read.
				//
				// Our memory model guarantee if we pick up the change in bucket from another processor,
				// we will see the 'isWriterProgress' flag to be true or 'version' is changed in the reader.
				//

				b = lbuckets.Array[bucketNumber];

				if (b.key == null)
				{
					value = null;
					return false;
				}

				if (((b.hash_coll & 0x7FFFFFFF) == hashcode) &&
					KeyEquals(b.key, key))
				{
					value = b.val;
					return true;
				}

				bucketNumber = (int)(((long)bucketNumber + incr) % (uint)lbuckets.Length);
			} while (b.hash_coll < 0 && ++ntry < lbuckets.Length);

			value = null;
			return false;
		}

		// Increases the bucket count of this hashtable. This method is called from
		// the Insert method when the actual load factor of the hashtable reaches
		// the upper limit specified when the hashtable was constructed. The number
		// of buckets in the hashtable is increased to the smallest prime number
		// that is larger than twice the current number of buckets, and the entries
		// in the hashtable are redistributed into the new buckets using the cached
		// hashcodes.
		private void expand()
		{
			int rawsize = HashHelpers.ExpandPrime(_buckets.Length);
			rehash(rawsize);
		}

		// We occasionally need to rehash the table to clean up the collision bits.
		private void rehash()
		{
			rehash(_buckets.Length);
		}

		private void rehash(int newsize)
		{
			// reset occupancy
			_occupancy = 0;

			// Don't replace any internal state until we've finished adding to the
			// new bucket[].  This serves two purposes:
			//   1) Allow concurrent readers to see valid hashtable contents
			//      at all times
			//   2) Protect against an OutOfMemoryException while allocating this
			//      new bucket[].
			buckets newBuckets = new buckets(newsize);

			// Local alias for performance
			var bucketsArray = _buckets.Array;

			// rehash table into new buckets
			int nb;
			for (nb = 0; nb < _buckets.Length; nb++)
			{
				bucket oldb = bucketsArray[nb];
				if ((oldb.key != null) && (oldb.key != bucketsArray))
				{
					int hashcode = oldb.hash_coll & 0x7FFFFFFF;
					putEntry(ref newBuckets, oldb.key, oldb.val, hashcode);
				}
			}

			var previousBuckets = _buckets;
			// New bucket[] is good to go - replace buckets and other internal state.
			_buckets = newBuckets;

			// Return the bucket's array to the pool
			previousBuckets.Return();

			_loadsize = (int)(_loadFactor * newsize);
			// minimum size of hashtable is 3 now and maximum loadFactor is 0.72 now.
			Debug.Assert(_loadsize < newsize, "Our current implementation means this is not possible.");
		}

		// Returns an enumerator for this hashtable.
		// If modifications made to the hashtable while an enumeration is
		// in progress, the MoveNext and Current methods of the
		// enumerator will throw an exception.
		//
		IEnumerator IEnumerable.GetEnumerator()
		{
			return new HashtableEnumerator(this, HashtableEnumerator.DictEntry);
		}

		// Returns a dictionary enumerator for this hashtable.
		// If modifications made to the hashtable while an enumeration is
		// in progress, the MoveNext and Current methods of the
		// enumerator will throw an exception.
		//
		public virtual IDictionaryEnumerator GetEnumerator()
		{
			return new HashtableEnumerator(this, HashtableEnumerator.DictEntry);
		}

		// Internal method to get the hash code for an Object.  This will call
		// GetHashCode() on each object if you haven't provided an IHashCodeProvider
		// instance.  Otherwise, it calls hcp.GetHashCode(obj).
		protected virtual int GetHash(object key)
		{
			if (_keycomparer != null)
				return _keycomparer.GetHashCode(key);
			return key.GetHashCode();
		}

		// Is this Hashtable read-only?
		public virtual bool IsReadOnly => false;

		public virtual bool IsFixedSize => false;

		// Is this Hashtable synchronized?  See SyncRoot property
		public virtual bool IsSynchronized => false;

		// Internal method to compare two keys.  If you have provided an IComparer
		// instance in the constructor, this method will call comparer.Compare(item, key).
		// Otherwise, it will call item.Equals(key).
		//
		protected virtual bool KeyEquals(object? item, object key)
		{
			Debug.Assert(key != null, "key can't be null here!");
			if (object.ReferenceEquals(_buckets.Array, item))
			{
				return false;
			}

			if (object.ReferenceEquals(item, key))
				return true;

			if (_keycomparer != null)
				return _keycomparer.Equals(item, key);
			return item == null ? false : item.Equals(key);
		}

		// Returns a collection representing the keys of this hashtable. The order
		// in which the returned collection represents the keys is unspecified, but
		// it is guaranteed to be          buckets = newBuckets; the same order in which a collection returned by
		// GetValues represents the values of the hashtable.
		//
		// The returned collection is live in the sense that any changes
		// to the hash table are reflected in this collection.  It is not
		// a static copy of all the keys in the hash table.
		//
		public virtual ICollection Keys => _keys ??= new KeyCollection(this);

		// Returns a collection representing the values of this hashtable. The
		// order in which the returned collection represents the values is
		// unspecified, but it is guaranteed to be the same order in which a
		// collection returned by GetKeys represents the keys of the
		// hashtable.
		//
		// The returned collection is live in the sense that any changes
		// to the hash table are reflected in this collection.  It is not
		// a static copy of all the keys in the hash table.
		//
		public virtual ICollection Values => _values ??= new ValueCollection(this);

		// Inserts an entry into this hashtable. This method is called from the Set
		// and Add methods. If the add parameter is true and the given key already
		// exists in the hashtable, an exception is thrown.
		private void Insert(object key, object? nvalue, bool add)
		{
			if (key == null)
			{
				throw new ArgumentNullException(nameof(key), "SR.ArgumentNull_Key");
			}

			if (_count >= _loadsize)
			{
				expand();
			}
			else if (_occupancy > _loadsize && _count > 100)
			{
				rehash();
			}

			// Local alias for access performance
			var bucketsArray = _buckets.Array;

			// Assume we only have one thread writing concurrently.  Modify
			// buckets to contain new data, as long as we insert in the right order.
			uint hashcode = InitHash(key, _buckets.Length, out uint seed, out uint incr);
			int ntry = 0;
			int emptySlotNumber = -1; // We use the empty slot number to cache the first empty slot. We chose to reuse slots
									  // create by remove that have the collision bit set over using up new slots.
			int bucketNumber = (int)(seed % (uint)_buckets.Length);
			do
			{
				// Set emptySlot number to current bucket if it is the first available bucket that we have seen
				// that once contained an entry and also has had a collision.
				// We need to search this entire collision chain because we have to ensure that there are no
				// duplicate entries in the table.
				if (emptySlotNumber == -1 && (bucketsArray[bucketNumber].key == bucketsArray) && (bucketsArray[bucketNumber].hash_coll < 0))// (((buckets[bucketNumber].hash_coll & unchecked(0x80000000))!=0)))
					emptySlotNumber = bucketNumber;

				// Insert the key/value pair into this bucket if this bucket is empty and has never contained an entry
				// OR
				// This bucket once contained an entry but there has never been a collision
				if ((bucketsArray[bucketNumber].key == null) ||
					(bucketsArray[bucketNumber].key == bucketsArray && ((bucketsArray[bucketNumber].hash_coll & unchecked(0x80000000)) == 0)))
				{
					// If we have found an available bucket that has never had a collision, but we've seen an available
					// bucket in the past that has the collision bit set, use the previous bucket instead
					if (emptySlotNumber != -1) // Reuse slot
						bucketNumber = emptySlotNumber;

					// We pretty much have to insert in this order.  Don't set hash
					// code until the value & key are set appropriately.
					bucketsArray[bucketNumber].val = nvalue;
					bucketsArray[bucketNumber].key = key;
					bucketsArray[bucketNumber].hash_coll |= (int)hashcode;
					_count++;

					return;
				}

				// The current bucket is in use
				// OR
				// it is available, but has had the collision bit set and we have already found an available bucket
				if (((bucketsArray[bucketNumber].hash_coll & 0x7FFFFFFF) == hashcode) &&
					KeyEquals(bucketsArray[bucketNumber].key, key))
				{
					if (add)
					{
						throw new ArgumentException("SR.Argument_AddingDuplicate__"); // SR.Format("SR.Argument_AddingDuplicate__", _buckets[bucketNumber].key, key));
					}
					bucketsArray[bucketNumber].val = nvalue;

					return;
				}

				// The current bucket is full, and we have therefore collided.  We need to set the collision bit
				// unless we have remembered an available slot previously.
				if (emptySlotNumber == -1)
				{// We don't need to set the collision bit here since we already have an empty slot
					if (bucketsArray[bucketNumber].hash_coll >= 0)
					{
						bucketsArray[bucketNumber].hash_coll |= unchecked((int)0x80000000);
						_occupancy++;
					}
				}

				bucketNumber = (int)(((long)bucketNumber + incr) % (uint)_buckets.Length);
			} while (++ntry < _buckets.Length);

			// This code is here if and only if there were no buckets without a collision bit set in the entire table
			if (emptySlotNumber != -1)
			{
				// We pretty much have to insert in this order.  Don't set hash
				// code until the value & key are set appropriately.
				bucketsArray[emptySlotNumber].val = nvalue;
				bucketsArray[emptySlotNumber].key = key;
				bucketsArray[emptySlotNumber].hash_coll |= (int)hashcode;
				_count++;

				return;
			}

			// If you see this assert, make sure load factor & count are reasonable.
			// Then verify that our double hash function (h2, described at top of file)
			// meets the requirements described above. You should never see this assert.
			Debug.Fail("hash table insert failed!  Load factor too high, or our double hashing function is incorrect.");
			throw new InvalidOperationException("SR.InvalidOperation_HashInsertFailed");
		}

		private void putEntry(ref buckets newBuckets, object key, object? nvalue, int hashcode)
		{
			Debug.Assert(hashcode >= 0, "hashcode >= 0");  // make sure collision bit (sign bit) wasn't set.

			// Local alias for access speed
			var newBucketsArray = newBuckets.Array;

			uint seed = (uint)hashcode;
			uint incr = unchecked((uint)(1 + ((seed * HashHelpers.HashPrime) % ((uint)newBuckets.Length - 1))));
			int bucketNumber = (int)(seed % (uint)newBuckets.Length);
			while (true)
			{
				if ((newBucketsArray[bucketNumber].key == null) || (newBucketsArray[bucketNumber].key == _buckets.Array))
				{
					newBucketsArray[bucketNumber].val = nvalue;
					newBucketsArray[bucketNumber].key = key;
					newBucketsArray[bucketNumber].hash_coll |= hashcode;
					return;
				}

				if (newBucketsArray[bucketNumber].hash_coll >= 0)
				{
					newBucketsArray[bucketNumber].hash_coll |= unchecked((int)0x80000000);
					_occupancy++;
				}
				bucketNumber = (int)(((long)bucketNumber + incr) % (uint)newBuckets.Length);
			}
		}

		// Removes an entry from this hashtable. If an entry with the specified
		// key exists in the hashtable, it is removed. An ArgumentException is
		// thrown if the key is null.
		//
		public virtual void Remove(object key)
		{
			if (key == null)
			{
				throw new ArgumentNullException(nameof(key), "SR.ArgumentNull_Key");
			}

			// Assuming only one concurrent writer, write directly into buckets.
			uint hashcode = InitHash(key, _buckets.Length, out uint seed, out uint incr);
			int ntry = 0;

			// Local alias for access speed
			var bucketsArray = _buckets.Array;

			bucket b;
			int bn = (int)(seed % (uint)_buckets.Length);  // bucketNumber
			do
			{
				b = bucketsArray[bn];
				if (((b.hash_coll & 0x7FFFFFFF) == hashcode) &&
					KeyEquals(b.key, key))
				{
					// Clear hash_coll field, then key, then value
					bucketsArray[bn].hash_coll &= unchecked((int)0x80000000);
					if (bucketsArray[bn].hash_coll != 0)
					{
						bucketsArray[bn].key = bucketsArray;
					}
					else
					{
						bucketsArray[bn].key = null;
					}
					bucketsArray[bn].val = null;  // Free object references sooner & simplify ContainsValue.
					_count--;
					return;
				}
				bn = (int)(((long)bn + incr) % (uint)_buckets.Length);
			} while (b.hash_coll < 0 && ++ntry < _buckets.Length);
		}

		// Returns the object to synchronize on for this hash table.
		public virtual object SyncRoot => this;

		// Returns the number of associations in this hashtable.
		//
		public virtual int Count => _count;

		// Implements a Collection for the keys of a hashtable. An instance of this
		// class is created by the GetKeys method of a hashtable.
		private sealed class KeyCollection : ICollection
		{
			private readonly HashtableEx _hashtable;

			internal KeyCollection(HashtableEx hashtable)
			{
				_hashtable = hashtable;
			}

			public void CopyTo(Array array, int arrayIndex)
			{
				if (array == null)
					throw new ArgumentNullException(nameof(array));
				if (array.Rank != 1)
					throw new ArgumentException("SR.Arg_RankMultiDimNotSupported", nameof(array));
				if (arrayIndex < 0)
					throw new ArgumentOutOfRangeException(nameof(arrayIndex), "SR.ArgumentOutOfRange_NeedNonNegNum");
				if (array.Length - arrayIndex < _hashtable._count)
					throw new ArgumentException("SR.Arg_ArrayPlusOffTooSmall");
				_hashtable.CopyKeys(array, arrayIndex);
			}

			public IEnumerator GetEnumerator()
			{
				return new HashtableEnumerator(_hashtable, HashtableEnumerator.Keys);
			}

			public bool IsSynchronized => _hashtable.IsSynchronized;

			public object SyncRoot => _hashtable.SyncRoot;

			public int Count => _hashtable._count;
		}

		// Implements a Collection for the values of a hashtable. An instance of
		// this class is created by the GetValues method of a hashtable.
		private sealed class ValueCollection : ICollection
		{
			private readonly HashtableEx _hashtable;

			internal ValueCollection(HashtableEx hashtable)
			{
				_hashtable = hashtable;
			}

			public void CopyTo(Array array, int arrayIndex)
			{
				if (array == null)
					throw new ArgumentNullException(nameof(array));
				if (array.Rank != 1)
					throw new ArgumentException("SR.Arg_RankMultiDimNotSupported", nameof(array));
				if (arrayIndex < 0)
					throw new ArgumentOutOfRangeException(nameof(arrayIndex), "SR.ArgumentOutOfRange_NeedNonNegNum");
				if (array.Length - arrayIndex < _hashtable._count)
					throw new ArgumentException("SR.Arg_ArrayPlusOffTooSmall");
				_hashtable.CopyValues(array, arrayIndex);
			}

			public IEnumerator GetEnumerator()
			{
				return new HashtableEnumerator(_hashtable, HashtableEnumerator.Values);
			}

			public bool IsSynchronized => _hashtable.IsSynchronized;

			public object SyncRoot => _hashtable.SyncRoot;

			public int Count => _hashtable._count;
		}

		// Implements an enumerator for a hashtable. The enumerator uses the
		// internal version number of the hashtable to ensure that no modifications
		// are made to the hashtable while an enumeration is in progress.
		private sealed class HashtableEnumerator : IDictionaryEnumerator, ICloneable
		{
			private readonly HashtableEx _hashtable;
			private int _bucket;
			private bool _current;
			private readonly int _getObjectRetType;   // What should GetObject return?
			private object? _currentKey;
			private object? _currentValue;

			internal const int Keys = 1;
			internal const int Values = 2;
			internal const int DictEntry = 3;

			internal HashtableEnumerator(HashtableEx hashtable, int getObjRetType)
			{
				_hashtable = hashtable;
				_bucket = hashtable._buckets.Length;
				_current = false;
				_getObjectRetType = getObjRetType;
			}

			public object Clone() => MemberwiseClone();

			public object Key
			{
				get
				{
					if (!_current)
						throw new InvalidOperationException("SR.InvalidOperation_EnumNotStarted");
					return _currentKey!;
				}
			}

			public bool MoveNext()
			{
				// Local alias for performance
				var bucketsArray = _hashtable._buckets.Array;

				while (_bucket > 0)
				{
					_bucket--;
					object? keyv = bucketsArray[_bucket].key;
					if ((keyv != null) && (keyv != bucketsArray))
					{
						_currentKey = keyv;
						_currentValue = bucketsArray[_bucket].val;
						_current = true;
						return true;
					}
				}
				_current = false;
				return false;
			}

			public DictionaryEntry Entry
			{
				get
				{
					if (!_current)
						throw new InvalidOperationException("SR.InvalidOperation_EnumOpCantHappen");
					return new DictionaryEntry(_currentKey!, _currentValue);
				}
			}

			public object? Current
			{
				get
				{
					if (!_current)
						throw new InvalidOperationException("SR.InvalidOperation_EnumOpCantHappen");

					if (_getObjectRetType == Keys)
						return _currentKey;
					else if (_getObjectRetType == Values)
						return _currentValue;
					else
						return new DictionaryEntry(_currentKey!, _currentValue);
				}
			}

			public object? Value
			{
				get
				{
					if (!_current)
						throw new InvalidOperationException("SR.InvalidOperation_EnumOpCantHappen");
					return _currentValue;
				}
			}

			public void Reset()
			{
				_current = false;
				_bucket = _hashtable._buckets.Length;
				_currentKey = null;
				_currentValue = null;
			}
		}

		// internal debug view class for hashtable
		internal sealed class HashtableDebugView
		{
			private readonly HashtableEx _hashtable;

			public HashtableDebugView(HashtableEx hashtable)
			{
				if (hashtable == null)
				{
					throw new ArgumentNullException(nameof(hashtable));
				}

				_hashtable = hashtable;
			}

			[DebuggerBrowsable(DebuggerBrowsableState.RootHidden)]
			public KeyValuePairs[] Items => _hashtable.ToKeyValuePairsArray();
		}
	}

	internal static partial class HashHelpers
	{
		public const uint HashCollisionThreshold = 100;

		// This is the maximum prime smaller than Array.MaxArrayLength
		public const int MaxPrimeArrayLength = 0x7FEFFFFD;

		public const int HashPrime = 101;

		// Table of prime numbers to use as hash table sizes.
		// A typical resize algorithm would pick the smallest prime number in this array
		// that is larger than twice the previous capacity.
		// Suppose our Hashtable currently has capacity x and enough elements are added
		// such that a resize needs to occur. Resizing first computes 2x then finds the
		// first prime in the table greater than 2x, i.e. if primes are ordered
		// p_1, p_2, ..., p_i, ..., it finds p_n such that p_n-1 < 2x < p_n.
		// Doubling is important for preserving the asymptotic complexity of the
		// hashtable operations such as add.  Having a prime guarantees that double
		// hashing does not lead to infinite loops.  IE, your hash function will be
		// h1(key) + i*h2(key), 0 <= i < size.  h2 and the size must be relatively prime.
		// We prefer the low computation costs of higher prime numbers over the increased
		// memory allocation of a fixed prime number i.e. when right sizing a HashSet.
		private static readonly int[] s_primes =
		{
			3, 7, 11, 17, 23, 29, 37, 47, 59, 71, 89, 107, 131, 163, 197, 239, 293, 353, 431, 521, 631, 761, 919,
			1103, 1327, 1597, 1931, 2333, 2801, 3371, 4049, 4861, 5839, 7013, 8419, 10103, 12143, 14591,
			17519, 21023, 25229, 30293, 36353, 43627, 52361, 62851, 75431, 90523, 108631, 130363, 156437,
			187751, 225307, 270371, 324449, 389357, 467237, 560689, 672827, 807403, 968897, 1162687, 1395263,
			1674319, 2009191, 2411033, 2893249, 3471899, 4166287, 4999559, 5999471, 7199369
		};

		public static bool IsPrime(int candidate)
		{
			if ((candidate & 1) != 0)
			{
				int limit = (int)Math.Sqrt(candidate);
				for (int divisor = 3; divisor <= limit; divisor += 2)
				{
					if ((candidate % divisor) == 0)
						return false;
				}
				return true;
			}
			return candidate == 2;
		}

		public static int GetPrime(int min)
		{
			if (min < 0)
				throw new ArgumentException("SR.Arg_HTCapacityOverflow");

			foreach (int prime in s_primes)
			{
				if (prime >= min)
					return prime;
			}

			// Outside of our predefined table. Compute the hard way.
			for (int i = (min | 1); i < int.MaxValue; i += 2)
			{
				if (IsPrime(i) && ((i - 1) % HashPrime != 0))
					return i;
			}
			return min;
		}

		// Returns size of hashtable to grow to.
		public static int ExpandPrime(int oldSize)
		{
			int newSize = 2 * oldSize;

			// Allow the hashtables to grow to maximum possible size (~2G elements) before encountering capacity overflow.
			// Note that this check works even when _items.Length overflowed thanks to the (uint) cast
			if ((uint)newSize > MaxPrimeArrayLength && MaxPrimeArrayLength > oldSize)
			{
				Debug.Assert(MaxPrimeArrayLength == GetPrime(MaxPrimeArrayLength), "Invalid MaxPrimeArrayLength");
				return MaxPrimeArrayLength;
			}

			return GetPrime(newSize);
		}

		/// <summary>Returns approximate reciprocal of the divisor: ceil(2**64 / divisor).</summary>
		/// <remarks>This should only be used on 64-bit.</remarks>
		public static ulong GetFastModMultiplier(uint divisor) =>
			ulong.MaxValue / divisor + 1;

		/// <summary>Performs a mod operation using the multiplier pre-computed with <see cref="GetFastModMultiplier"/>.</summary>
		/// <remarks>This should only be used on 64-bit.</remarks>
		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public static uint FastMod(uint value, uint divisor, ulong multiplier)
		{
			// We use modified Daniel Lemire's fastmod algorithm (https://github.com/dotnet/runtime/pull/406),
			// which allows to avoid the long multiplication if the divisor is less than 2**31.
			Debug.Assert(divisor <= int.MaxValue);

			// This is equivalent of (uint)Math.BigMul(multiplier * value, divisor, out _). This version
			// is faster than BigMul currently because we only need the high bits.
			uint highbits = (uint)(((((multiplier * value) >> 32) + 1) * divisor) >> 32);

			Debug.Assert(highbits == value % divisor);
			return highbits;
		}
	}

	[DebuggerDisplay("{_value}", Name = "[{_key}]")]
	internal sealed class KeyValuePairs
	{
		[DebuggerBrowsable(DebuggerBrowsableState.Never)]
		private readonly object _key;

		[DebuggerBrowsable(DebuggerBrowsableState.Never)]
		private readonly object? _value;

		public KeyValuePairs(object key, object? value)
		{
			_value = value;
			_key = key;
		}
	}
}
