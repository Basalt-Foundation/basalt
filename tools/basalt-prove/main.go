// Command basalt-prove generates a golden Groth16 verification key + proof with gnark, serialized in
// Basalt's Groth16Codec layout (compressed BLS12-381 points), and emits it as JSON. Basalt's
// blst-backed Groth16Verifier must accept it, which is what the C# GnarkCrossEncodingTests assert.
//
// The circuit is deliberately trivial (prove knowledge of X such that X*X == Y, Y public) so the test
// isolates the gnark<->blst point ENCODING, not any circuit logic.
package main

import (
	"encoding/binary"
	"encoding/hex"
	"encoding/json"
	"fmt"
	"os"

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
	default:
		fmt.Fprintf(os.Stderr, "unknown mode %q (want: encoding | compliance)\n", mode)
		os.Exit(1)
	}
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
