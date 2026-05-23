namespace PatientFlow.Common.Exceptions;

public class PatientNotFoundException(int id) : Exception($"Patient with id {id} not found")
{
}

public class DuplicateEmailException(string email) : Exception($"Patient/User with email {email} already exists")
{
}
