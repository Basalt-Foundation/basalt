// Command basalt-prove generates a golden Groth16 verification key + proof with gnark, serialized in
// Basalt's Groth16Codec layout (compressed BLS12-381 points), and emits it as JSON. Basalt's
// blst-backed Groth16Verifier must accept it, which is what the C# GnarkCrossEncodingTests assert.
//
// The circuit is deliberately trivial (prove knowledge of X such that X*X == Y, Y public) so the test
// isolates the gnark<->blst point ENCODING, not any circuit logic.
package main

import (
	"crypto/sha256"
	"encoding/binary"
	"encoding/hex"
	"encoding/json"
	"fmt"
	"os"
	"strings"

	"github.com/consensys/gnark-crypto/ecc"
	"github.com/consensys/gnark-crypto/ecc/bls12-381/fr"
	frmimc "github.com/consensys/gnark-crypto/ecc/bls12-381/fr/mimc"
	"github.com/consensys/gnark/backend/groth16"
	groth16bls12381 "github.com/consensys/gnark/backend/groth16/bls12-381"
	"github.com/consensys/gnark/frontend"
	"github.com/consensys/gnark/frontend/cs/r1cs"
	"github.com/consensys/gnark/logger"
	"github.com/consensys/gnark/std/hash/mimc"
)

// Domain separators for deriving the request-binding field elements. These MUST match the C# side
// (Trilith.KeyGate.RequestBinding) byte-for-byte: field = SHA256(domain || input) reduced mod r.
const (
	cidDomain       = "trilith-keygate-cid-v2"
	recipientDomain = "trilith-keygate-recipient-v2"
)

// reduceToField maps arbitrary bytes to a BLS12-381 scalar: SHA256(domain || data) interpreted big-endian
// and reduced mod r (fr.Element.SetBytes does the reduction). The C# verifier recomputes this identically.
func reduceToField(domain string, data []byte) fr.Element {
	h := sha256.New()
	h.Write([]byte(domain))
	h.Write(data)
	var e fr.Element
	e.SetBytes(h.Sum(nil))
	return e
}

// fieldFromCid binds a proof to a specific content id; fieldFromRecipientKey binds the wrapped key to a
// specific X25519 recipient public key.
func fieldFromCid(cid string) fr.Element          { return reduceToField(cidDomain, []byte(cid)) }
func fieldFromRecipientKey(key []byte) fr.Element { return reduceToField(recipientDomain, key) }

// The fixed request binding baked into the golden membership vector (mode `membership`). The same cid and
// recipient public key appear in the Trilith KeyGate test vector, whose private key unwraps the result.
const (
	goldenCid             = "bagaybqabciqopr4hislq6p6zfb7u4ddve6ws5m7j56qr6ldiarp74y2zpt6rcmy"
	goldenRecipientKeyHex = "87b6fc9b465c6b1404b71ae1c538f6099d494db31b414afb02476cba256ef754"
)

// SquareCircuit proves knowledge of X such that X*X == Y, with Y a public input.
// Used by the pure gnark<->blst encoding golden test (GnarkCrossEncodingTests).
type SquareCircuit struct {
	X frontend.Variable `gnark:",secret"`
	Y frontend.Variable `gnark:",public"`
}

func (c *SquareCircuit) Define(api frontend.API) error {
	api.AssertIsEqual(api.Mul(c.X, c.X), c.Y)
	return nil
}

// ComplianceCircuitV1 exposes the frozen compliance public-input layout. Public inputs are declared in
// the order the on-chain verifier reads them (CircuitV1Layout): IssuerRoot, Nullifier, Expiry, IssuerTier,
// RevocationRoot. The only binding constraint here is the nullifier derivation (Nullifier == MiMC(Secret,
// Epoch)); Merkle membership and expiry/tier range checks are added in the full circuit (3B.7). This
// suffices to freeze the layout and prove Basalt verifies a compliance-shaped 5-public-input proof.
type ComplianceCircuitV1 struct {
	IssuerRoot     frontend.Variable `gnark:",public"`
	Nullifier      frontend.Variable `gnark:",public"`
	Expiry         frontend.Variable `gnark:",public"`
	IssuerTier     frontend.Variable `gnark:",public"`
	RevocationRoot frontend.Variable `gnark:",public"`

	Secret frontend.Variable `gnark:",secret"`
	Epoch  frontend.Variable `gnark:",secret"`
}

func (c *ComplianceCircuitV1) Define(api frontend.API) error {
	h, err := mimc.NewMiMC(api)
	if err != nil {
		return err
	}
	h.Write(c.Secret, c.Epoch)
	api.AssertIsEqual(c.Nullifier, h.Sum())
	return nil
}

// MerkleDepth is the fixed issuer-tree depth for the membership circuit (2^depth leaves).
const MerkleDepth = 4

// RevDepth is the fixed revocation sparse-Merkle-tree depth. The credential's revocation slot is the low
// RevDepth bits of MiMC(secret); non-membership proves that slot's leaf is empty (0) in revocationRoot.
// A 32-bit slot keyspace has negligible collisions at testnet scale (mainnet would use a full-width or
// indexed tree).
const RevDepth = 32

// ComplianceCircuitMembership binds the whole credential to the issuer AND binds the proof to a specific
// request (content id + recipient). The credential commitment leaf = MiMC(Secret, Expiry, IssuerTier) must
// be a member of the issuer's Merkle tree whose root is the public IssuerRoot, proven by a private Merkle
// path. Because Expiry and IssuerTier are hashed into the leaf, the prover cannot change them without
// breaking membership, so IssuerRoot, Expiry, and IssuerTier are ALL constrained (tampering any of them
// fails). The nullifier binds CidField and RecipientKeyHash: nullifier = MiMC(Secret, CidField,
// RecipientKeyHash). This is the v2 request binding (7 public inputs): a captured proof cannot be
// redirected to a different content id or a different recipient key, because either change alters the
// nullifier the circuit proved and the pairing check rejects it.
type ComplianceCircuitMembership struct {
	IssuerRoot       frontend.Variable `gnark:",public"`
	Nullifier        frontend.Variable `gnark:",public"`
	Expiry           frontend.Variable `gnark:",public"`
	IssuerTier       frontend.Variable `gnark:",public"`
	RevocationRoot   frontend.Variable `gnark:",public"`
	CidField         frontend.Variable `gnark:",public"` // binds the proof to one content id
	RecipientKeyHash frontend.Variable `gnark:",public"` // binds the wrapped key to one recipient

	Secret       frontend.Variable
	PathElements [MerkleDepth]frontend.Variable // issuer-tree sibling hash at each level
	PathIndices  [MerkleDepth]frontend.Variable // 0 = current node is the left child, 1 = the right child
	RevSiblings  [RevDepth]frontend.Variable    // revocation SMT sibling hash at each level
}

func (c *ComplianceCircuitMembership) Define(api frontend.API) error {
	h, err := mimc.NewMiMC(api)
	if err != nil {
		return err
	}

	// Credential commitment leaf = MiMC(Secret, Expiry, IssuerTier). Hashing Expiry and IssuerTier into
	// the leaf binds them: a prover cannot change the public Expiry/IssuerTier without changing the leaf,
	// which breaks the Merkle membership below.
	h.Reset()
	h.Write(c.Secret, c.Expiry, c.IssuerTier)
	cur := h.Sum()

	// Walk the Merkle path up to the root, hashing MiMC(left, right) at each level.
	for i := 0; i < MerkleDepth; i++ {
		api.AssertIsBoolean(c.PathIndices[i])
		left := api.Select(c.PathIndices[i], c.PathElements[i], cur)
		right := api.Select(c.PathIndices[i], cur, c.PathElements[i])
		h.Reset()
		h.Write(left, right)
		cur = h.Sum()
	}
	api.AssertIsEqual(cur, c.IssuerRoot) // binds IssuerRoot to the credential

	// Nullifier = MiMC(Secret, CidField, RecipientKeyHash). Folding the request bindings into the nullifier
	// makes CidField and RecipientKeyHash genuinely constrained public inputs (their IC points are not the
	// identity), so the verifier can trust them: changing either one changes the proven nullifier and the
	// pairing check fails. The single-use nullifier is thus scoped to (credential, cid, recipient).
	h.Reset()
	h.Write(c.Secret, c.CidField, c.RecipientKeyHash)
	api.AssertIsEqual(c.Nullifier, h.Sum())

	// Revocation non-membership: the credential's revocation slot (low RevDepth bits of MiMC(Secret)) has
	// an empty (0) leaf in the revocation SMT whose root is the public RevocationRoot. If the credential
	// were revoked, that leaf would be non-zero and the empty leaf would not hash to RevocationRoot.
	h.Reset()
	h.Write(c.Secret)
	revId := h.Sum()
	revBits := api.ToBinary(revId) // full field decomposition; the low RevDepth bits are the slot path
	revCur := frontend.Variable(0) // empty leaf = not revoked
	for i := 0; i < RevDepth; i++ {
		bit := revBits[i]
		left := api.Select(bit, c.RevSiblings[i], revCur)
		right := api.Select(bit, revCur, c.RevSiblings[i])
		h.Reset()
		h.Write(left, right)
		revCur = h.Sum()
	}
	api.AssertIsEqual(revCur, c.RevocationRoot)

	return nil
}

type golden struct {
	Description   string   `json:"description"`
	Curve         string   `json:"curve"`
	Vk            string   `json:"vk_hex"`
	Proof         string   `json:"proof_hex"`
	PublicInputs  []string `json:"public_inputs_hex"`
	ExpectedValid bool     `json:"expected_valid"`
}

func must(err error, ctx string) {
	if err != nil {
		fmt.Fprintf(os.Stderr, "%s: %v\n", ctx, err)
		os.Exit(1)
	}
}

func main() {
	logger.Disable() // keep stdout pure JSON

	mode := "encoding"
	if len(os.Args) > 1 {
		mode = os.Args[1]
	}
	switch mode {
	case "encoding":
		genEncoding()
	case "compliance":
		genCompliance()
	case "membership":
		genMembership()
	case "setup":
		genSetup()
	case "prove":
		genProve()
	default:
		fmt.Fprintf(os.Stderr, "unknown mode %q (want: encoding | compliance | membership | setup | prove)\n", mode)
		os.Exit(1)
	}
}

// proveInput is the JSON a prover feeds to `prove` on stdin: their private credential, the specific
// request being authorized (content id + recipient X25519 public key), plus the issuer's published tree
// leaves (so the tool can build the membership path).
type proveInput struct {
	Secret           uint64   `json:"secret"`
	Expiry           uint64   `json:"expiry"`
	Tier             uint64   `json:"tier"`
	Cid              string   `json:"cid"`              // the content id this proof authorizes
	RecipientKeyHex  string   `json:"recipientKeyHex"`  // the recipient X25519 public key (hex, 32 bytes)
	LeafIndex        int      `json:"leafIndex"`
	Leaves           []string `json:"leaves"` // 2^MerkleDepth hex field elements (the issuer's published tree)
}

// complianceProofOut is what a transaction's ComplianceProof carries: the Groth16 proof, the public
// inputs concatenated, and the nullifier.
type complianceProofOut struct {
	Description     string `json:"description"`
	ProofHex        string `json:"proof_hex"`         // 192 bytes: A(48) B(96) C(48)
	PublicInputsHex string `json:"public_inputs_hex"` // flat 7*32 = 224 bytes
	NullifierHex    string `json:"nullifier_hex"`     // 32 bytes, equals public input index 1
	VkHex           string `json:"vk_hex,omitempty"`  // Basalt-layout VK (setup only)
}

func appendG1(dst []byte, b [48]byte) []byte { return append(dst, b[:]...) }
func appendG2(dst []byte, b [96]byte) []byte { return append(dst, b[:]...) }

// serializeVK writes the VK in Basalt's Groth16Codec order using compressed point bytes:
// AlphaG1(48) BetaG2(96) GammaG2(96) DeltaG2(96) IC_count(4 LE) IC[i](48)...
func serializeVK(vkc *groth16bls12381.VerifyingKey) []byte {
	var b []byte
	alpha := vkc.G1.Alpha.Bytes()
	beta := vkc.G2.Beta.Bytes()
	gamma := vkc.G2.Gamma.Bytes()
	delta := vkc.G2.Delta.Bytes()
	b = appendG1(b, alpha)
	b = appendG2(b, beta)
	b = appendG2(b, gamma)
	b = appendG2(b, delta)
	icCount := make([]byte, 4)
	binary.LittleEndian.PutUint32(icCount, uint32(len(vkc.G1.K)))
	b = append(b, icCount...)
	for _, k := range vkc.G1.K {
		kb := k.Bytes()
		b = appendG1(b, kb)
	}
	return b
}

// serializeProof writes A(48) B(96) C(48).
func serializeProof(proofc *groth16bls12381.Proof) []byte {
	var b []byte
	ar := proofc.Ar.Bytes()
	bs := proofc.Bs.Bytes()
	krs := proofc.Krs.Bytes()
	b = appendG1(b, ar)
	b = appendG2(b, bs)
	b = appendG1(b, krs)
	return b
}

// publicInputsHex returns each public input as a 32-byte big-endian hex string, in gnark's canonical
// public-witness order (the same order Basalt must pass them to Groth16Verifier.Verify).
func publicInputsHex(publicWitness witnessVector) []string {
	vec := publicWitness.Vector().(fr.Vector)
	out := make([]string, len(vec))
	for i := range vec {
		bytes := vec[i].Bytes()
		out[i] = hex.EncodeToString(bytes[:])
	}
	return out
}

// witnessVector is the subset of gnark's witness.Witness we use.
type witnessVector interface{ Vector() any }

func emit(out golden) {
	enc := json.NewEncoder(os.Stdout)
	enc.SetIndent("", "  ")
	must(enc.Encode(out), "encode json")
}

func genEncoding() {
	field := ecc.BLS12_381.ScalarField()
	ccs, err := frontend.Compile(field, r1cs.NewBuilder, &SquareCircuit{})
	must(err, "compile")
	pk, vk, err := groth16.Setup(ccs)
	must(err, "setup")

	assignment := &SquareCircuit{X: 3, Y: 9} // 3*3 == 9
	fullWitness, err := frontend.NewWitness(assignment, field)
	must(err, "witness")
	publicWitness, err := fullWitness.Public()
	must(err, "public witness")
	proof, err := groth16.Prove(ccs, pk, fullWitness)
	must(err, "prove")
	must(groth16.Verify(proof, vk, publicWitness), "gnark verify")

	vkc := vk.(*groth16bls12381.VerifyingKey)
	proofc := proof.(*groth16bls12381.Proof)

	emit(golden{
		Description:   "gnark v0.15 Groth16 BLS12-381, circuit X*X==Y (Y=9), Basalt Groth16Codec layout",
		Curve:         "BLS12-381",
		Vk:            hex.EncodeToString(serializeVK(vkc)),
		Proof:         hex.EncodeToString(serializeProof(proofc)),
		PublicInputs:  publicInputsHex(publicWitness),
		ExpectedValid: true,
	})
}

// mimcNullifier computes MiMC(secret, epoch) off-circuit to match the in-circuit constraint.
func mimcNullifier(secret, epoch fr.Element) fr.Element {
	h := frmimc.NewMiMC()
	sb := secret.Bytes()
	eb := epoch.Bytes()
	_, _ = h.Write(sb[:])
	_, _ = h.Write(eb[:])
	var n fr.Element
	n.SetBytes(h.Sum(nil))
	return n
}

func genCompliance() {
	field := ecc.BLS12_381.ScalarField()
	ccs, err := frontend.Compile(field, r1cs.NewBuilder, &ComplianceCircuitV1{})
	must(err, "compile")
	pk, vk, err := groth16.Setup(ccs)
	must(err, "setup")

	var secret, epoch fr.Element
	secret.SetUint64(42)
	epoch.SetUint64(7)
	nullifier := mimcNullifier(secret, epoch)

	// Distinctive, predictable small values for expiry/tier so the frozen index order is testable.
	var issuerRoot, revocationRoot fr.Element
	issuerRoot.SetUint64(0x1111_1111)
	revocationRoot.SetUint64(0x5555_5555)
	const expiry = uint64(1893456000) // 2030-01-01T00:00:00Z
	const tier = uint64(3)

	assignment := &ComplianceCircuitV1{
		IssuerRoot:     issuerRoot,
		Nullifier:      nullifier,
		Expiry:         expiry,
		IssuerTier:     tier,
		RevocationRoot: revocationRoot,
		Secret:         secret,
		Epoch:          epoch,
	}
	fullWitness, err := frontend.NewWitness(assignment, field)
	must(err, "witness")
	publicWitness, err := fullWitness.Public()
	must(err, "public witness")
	proof, err := groth16.Prove(ccs, pk, fullWitness)
	must(err, "prove")
	must(groth16.Verify(proof, vk, publicWitness), "gnark verify")

	vkc := vk.(*groth16bls12381.VerifyingKey)
	proofc := proof.(*groth16bls12381.Proof)

	emit(golden{
		Description:   "gnark v0.15 Groth16 BLS12-381, ComplianceCircuitV1 (5 public inputs: issuerRoot,nullifier,expiry=1893456000,tier=3,revocationRoot), Basalt Groth16Codec layout",
		Curve:         "BLS12-381",
		Vk:            hex.EncodeToString(serializeVK(vkc)),
		Proof:         hex.EncodeToString(serializeProof(proofc)),
		PublicInputs:  publicInputsHex(publicWitness),
		ExpectedValid: true,
	})
}

// mimcHash2 = MiMC(a, b); mimcHash3 = MiMC(a, b, c). Off-circuit counterparts of the in-circuit hashing,
// used to build the issuer tree (node hashes and the credential leaf) so its root matches what the
// circuit recomputes.
func mimcHash2(a, b fr.Element) fr.Element {
	h := frmimc.NewMiMC()
	ab := a.Bytes()
	bb := b.Bytes()
	_, _ = h.Write(ab[:])
	_, _ = h.Write(bb[:])
	var out fr.Element
	out.SetBytes(h.Sum(nil))
	return out
}

func mimcHash3(a, b, c fr.Element) fr.Element {
	h := frmimc.NewMiMC()
	ab := a.Bytes()
	bb := b.Bytes()
	cb := c.Bytes()
	_, _ = h.Write(ab[:])
	_, _ = h.Write(bb[:])
	_, _ = h.Write(cb[:])
	var out fr.Element
	out.SetBytes(h.Sum(nil))
	return out
}

// revocationDefaults returns the default (empty-subtree) hash at each revocation-SMT level: d[0] = 0
// (empty leaf), d[i] = MiMC(d[i-1], d[i-1]). d[RevDepth] is the root of a fully-empty tree (no revocations).
func revocationDefaults() []fr.Element {
	d := make([]fr.Element, RevDepth+1)
	d[0].SetZero()
	for i := 1; i <= RevDepth; i++ {
		d[i] = mimcHash2(d[i-1], d[i-1])
	}
	return d
}

// buildTreeAndPath builds a depth-`depth` MiMC Merkle tree over `leaves` (len == 2^depth) and returns the
// root plus the authentication path (sibling per level and the left/right index bit) for `index`.
func buildTreeAndPath(leaves []fr.Element, index, depth int) (root fr.Element, pathElements []fr.Element, pathIndices []int) {
	layer := leaves
	idx := index
	for d := 0; d < depth; d++ {
		pathElements = append(pathElements, layer[idx^1])
		pathIndices = append(pathIndices, idx&1)
		next := make([]fr.Element, len(layer)/2)
		for j := range next {
			next[j] = mimcHash2(layer[2*j], layer[2*j+1])
		}
		layer = next
		idx /= 2
	}
	return layer[0], pathElements, pathIndices
}

func genMembership() {
	field := ecc.BLS12_381.ScalarField()
	ccs, err := frontend.Compile(field, r1cs.NewBuilder, &ComplianceCircuitMembership{})
	must(err, "compile")
	pk, vk, err := groth16.Setup(ccs)
	must(err, "setup")

	var secret fr.Element
	secret.SetUint64(42)

	var expiryE, tierE fr.Element
	expiryE.SetUint64(1893456000) // 2030-01-01
	tierE.SetUint64(3)

	// Request binding: the proof authorizes exactly this content id and this recipient key.
	recipientKey, err := hex.DecodeString(goldenRecipientKeyHex)
	must(err, "decode golden recipient key")
	cidField := fieldFromCid(goldenCid)
	recipientKeyHash := fieldFromRecipientKey(recipientKey)

	// Build a 16-leaf issuer tree; the credential commitment MiMC(secret, expiry, tier) sits at index 5
	// (0b0101, so the path exercises both left and right positions). Other leaves are arbitrary distinct.
	const leafIndex = 5
	nLeaves := 1 << MerkleDepth
	leaves := make([]fr.Element, nLeaves)
	for i := range leaves {
		leaves[i].SetUint64(uint64(1000 + i))
	}
	leaves[leafIndex] = mimcHash3(secret, expiryE, tierE) // credential commitment binds secret, expiry, tier
	root, pathElems, pathIdx := buildTreeAndPath(leaves, leafIndex, MerkleDepth)
	nullifier := mimcHash3(secret, cidField, recipientKeyHash) // nullifier binds the request (cid, recipient)

	// Empty revocation tree (nothing revoked): root is the all-empty default, and every sibling on the
	// credential's revocation path is the empty-subtree default, so the empty leaf hashes to the root.
	revDefaults := revocationDefaults()
	revocationRoot := revDefaults[RevDepth]

	assignment := &ComplianceCircuitMembership{
		IssuerRoot:       root,
		Nullifier:        nullifier,
		Expiry:           expiryE,
		IssuerTier:       tierE,
		RevocationRoot:   revocationRoot,
		CidField:         cidField,
		RecipientKeyHash: recipientKeyHash,
		Secret:           secret,
	}
	for i := 0; i < MerkleDepth; i++ {
		assignment.PathElements[i] = pathElems[i]
		assignment.PathIndices[i] = pathIdx[i]
	}
	for i := 0; i < RevDepth; i++ {
		assignment.RevSiblings[i] = revDefaults[i]
	}

	fullWitness, err := frontend.NewWitness(assignment, field)
	must(err, "witness")
	publicWitness, err := fullWitness.Public()
	must(err, "public witness")
	proof, err := groth16.Prove(ccs, pk, fullWitness)
	must(err, "prove")
	must(groth16.Verify(proof, vk, publicWitness), "gnark verify")

	vkc := vk.(*groth16bls12381.VerifyingKey)
	proofc := proof.(*groth16bls12381.Proof)

	emit(golden{
		Description:   "gnark v0.15 Groth16 BLS12-381, ComplianceCircuitMembership v2 (7 public inputs: issuerRoot,nullifier,expiry=1893456000,tier=3,revocationRoot,cidField,recipientKeyHash; issuerRoot+expiry+tier bound via MiMC Merkle membership leaf=MiMC(secret,expiry,tier) at index 5 depth 4; nullifier=MiMC(secret,cidField,recipientKeyHash) binds cid+recipient), Basalt Groth16Codec layout",
		Curve:         "BLS12-381",
		Vk:            hex.EncodeToString(serializeVK(vkc)),
		Proof:         hex.EncodeToString(serializeProof(proofc)),
		PublicInputs:  publicInputsHex(publicWitness),
		ExpectedValid: true,
	})
}

func emitJSON(v any) {
	enc := json.NewEncoder(os.Stdout)
	enc.SetIndent("", "  ")
	must(enc.Encode(v), "encode json")
}

// CircuitV1Nullifier mirrors CircuitV1Layout.Nullifier (public input index 1) for this tool.
const CircuitV1Nullifier = 1

// genSetup runs the trusted setup ONCE for the membership circuit and writes the fixed proving and
// verifying keys to membership.pk / membership.vk. The emitted vk_hex is what the issuer registers
// on-chain (SchemaRegistry); every proof made with membership.pk verifies against it.
func genSetup() {
	field := ecc.BLS12_381.ScalarField()
	ccs, err := frontend.Compile(field, r1cs.NewBuilder, &ComplianceCircuitMembership{})
	must(err, "compile")
	pk, vk, err := groth16.Setup(ccs)
	must(err, "setup")

	pkFile, err := os.Create("membership.pk")
	must(err, "create membership.pk")
	_, err = pk.WriteTo(pkFile)
	must(err, "write pk")
	must(pkFile.Close(), "close pk")

	vkFile, err := os.Create("membership.vk")
	must(err, "create membership.vk")
	_, err = vk.WriteTo(vkFile)
	must(err, "write vk")
	must(vkFile.Close(), "close vk")

	vkc := vk.(*groth16bls12381.VerifyingKey)
	emitJSON(complianceProofOut{
		Description: "wrote membership.pk and membership.vk; register vk_hex on-chain (SchemaRegistry)",
		VkHex:       hex.EncodeToString(serializeVK(vkc)),
	})
}

// genProve reads a credential + the issuer's published leaves from stdin, builds the witness, loads the
// fixed proving key from `setup`, produces a proof, self-verifies it against the fixed vk, and emits the
// pieces a Basalt ComplianceProof carries (proof, concatenated public inputs, nullifier).
func genProve() {
	field := ecc.BLS12_381.ScalarField()
	ccs, err := frontend.Compile(field, r1cs.NewBuilder, &ComplianceCircuitMembership{})
	must(err, "compile")

	var in proveInput
	must(json.NewDecoder(os.Stdin).Decode(&in), "decode stdin JSON")

	n := 1 << MerkleDepth
	if in.LeafIndex < 0 || in.LeafIndex >= n {
		fmt.Fprintf(os.Stderr, "leafIndex %d out of range [0,%d)\n", in.LeafIndex, n)
		os.Exit(1)
	}

	var secret, expiryE, tierE fr.Element
	secret.SetUint64(in.Secret)
	expiryE.SetUint64(in.Expiry)
	tierE.SetUint64(in.Tier)
	credLeaf := mimcHash3(secret, expiryE, tierE) // the prover's credential commitment

	// Request binding: derive the field elements for the content id and recipient key this proof authorizes.
	if in.Cid == "" {
		fmt.Fprintln(os.Stderr, "cid is required")
		os.Exit(1)
	}
	recipientKey, err := hex.DecodeString(strings.TrimPrefix(in.RecipientKeyHex, "0x"))
	must(err, "decode recipientKeyHex")
	if len(recipientKey) != 32 {
		fmt.Fprintf(os.Stderr, "recipientKeyHex must be 32 bytes, got %d\n", len(recipientKey))
		os.Exit(1)
	}
	cidField := fieldFromCid(in.Cid)
	recipientKeyHash := fieldFromRecipientKey(recipientKey)

	leaves := make([]fr.Element, n)
	if len(in.Leaves) == 0 {
		// Demo mode: synthesize an issuer tree with this credential at leafIndex, others arbitrary.
		for i := range leaves {
			leaves[i].SetUint64(uint64(1000 + i))
		}
		leaves[in.LeafIndex] = credLeaf
	} else if len(in.Leaves) == n {
		for i, hx := range in.Leaves {
			b, err := hex.DecodeString(strings.TrimPrefix(hx, "0x"))
			must(err, "decode leaf hex")
			leaves[i].SetBytes(b)
		}
		if !leaves[in.LeafIndex].Equal(&credLeaf) {
			fmt.Fprintf(os.Stderr, "leaves[%d] is not MiMC(secret, expiry, tier) for this credential\n", in.LeafIndex)
			os.Exit(1)
		}
	} else {
		fmt.Fprintf(os.Stderr, "leaves must be empty (demo tree) or have %d entries, got %d\n", n, len(in.Leaves))
		os.Exit(1)
	}

	root, pathElems, pathIdx := buildTreeAndPath(leaves, in.LeafIndex, MerkleDepth)
	nullifier := mimcHash3(secret, cidField, recipientKeyHash) // nullifier binds the request (cid, recipient)
	revDefaults := revocationDefaults()

	assignment := &ComplianceCircuitMembership{
		IssuerRoot:       root,
		Nullifier:        nullifier,
		Expiry:           expiryE,
		IssuerTier:       tierE,
		RevocationRoot:   revDefaults[RevDepth],
		CidField:         cidField,
		RecipientKeyHash: recipientKeyHash,
		Secret:           secret,
	}
	for i := 0; i < MerkleDepth; i++ {
		assignment.PathElements[i] = pathElems[i]
		assignment.PathIndices[i] = pathIdx[i]
	}
	for i := 0; i < RevDepth; i++ {
		assignment.RevSiblings[i] = revDefaults[i]
	}

	fullWitness, err := frontend.NewWitness(assignment, field)
	must(err, "witness")
	publicWitness, err := fullWitness.Public()
	must(err, "public witness")

	pk := groth16.NewProvingKey(ecc.BLS12_381)
	pkFile, err := os.Open("membership.pk")
	must(err, "open membership.pk (run `setup` first)")
	_, err = pk.ReadFrom(pkFile)
	must(err, "read pk")
	must(pkFile.Close(), "close pk")

	proof, err := groth16.Prove(ccs, pk, fullWitness)
	must(err, "prove")

	// Self-verify against the fixed verifying key.
	vk := groth16.NewVerifyingKey(ecc.BLS12_381)
	vkFile, err := os.Open("membership.vk")
	must(err, "open membership.vk")
	_, err = vk.ReadFrom(vkFile)
	must(err, "read vk")
	must(vkFile.Close(), "close vk")
	must(groth16.Verify(proof, vk, publicWitness), "self-verify against fixed vk")

	proofc := proof.(*groth16bls12381.Proof)
	pis := publicInputsHex(publicWitness)
	emitJSON(complianceProofOut{
		Description:     "compliance proof for the given credential (Basalt ComplianceProof pieces)",
		ProofHex:        hex.EncodeToString(serializeProof(proofc)),
		PublicInputsHex: strings.Join(pis, ""),
		NullifierHex:    pis[CircuitV1Nullifier],
	})
}
